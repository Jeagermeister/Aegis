using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aegis.Collectors;

/// <summary>
/// "Monitor the monitor" (design principle 5): a collector that dies silently is a blind spot, so
/// a separate sweep watches every <c>SourceSystem</c> heartbeat and raises a
/// <c>CollectorStale</c> alert when one goes quiet, then resolves it when the heartbeat returns.
/// The sweep is deliberately a different process path from the collectors it watches — a crash in
/// the collector host must not take the watcher down with it.
/// </summary>
public sealed class StaleCollectorSweepService : BackgroundService
{
    private readonly CollectorOptions _options;
    private readonly ILogger<StaleCollectorSweepService> _logger;

    public StaleCollectorSweepService(IOptions<CollectorOptions> options, ILogger<StaleCollectorSweepService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.StoreConnectionString))
        {
            _logger.LogWarning("No store connection string; the stale-collector sweep is disabled");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.PollIntervalSeconds));
        var staleAfter = _options.StaleAfterSeconds > 0
            ? TimeSpan.FromSeconds(_options.StaleAfterSeconds)
            : TimeSpan.FromSeconds(interval.TotalSeconds * 2);

        _logger.LogInformation("Stale-collector sweep running every {Interval}; a source is stale after {StaleAfter}",
            interval, staleAfter);

        using var timer = new PeriodicTimer(interval);
        var alerts = new AlertStore(_options.StoreConnectionString, _logger);

        while (await WaitForNextTickAsync(timer, stoppingToken))
        {
            try
            {
                await SweepAsync(alerts, staleAfter, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Stale-collector sweep failed");
            }
        }
    }

    internal async Task SweepAsync(AlertStore alerts, TimeSpan staleAfter, CancellationToken cancellationToken)    {
        await using var connection = new SqlConnection(_options.StoreConnectionString);
        var sources = (await connection.QueryAsync<(int Id, string Name, DateTime? LastHeartbeat)>(new CommandDefinition(@"
            SELECT Id, Name, LastHeartbeat
            FROM dbo.SourceSystem;",
            cancellationToken: cancellationToken))).ToList();

        var cutoff = DateTime.UtcNow - staleAfter;

        foreach (var source in sources)
        {
            var dedupKey = ErrorFingerprint.DedupKey(AlertType.CollectorStale, source.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

            if (source.LastHeartbeat is null || source.LastHeartbeat < cutoff)
            {
                var last = source.LastHeartbeat is { } heartbeat ? heartbeat.ToString("O") : "never";
                await alerts.RaiseAsync(
                    AlertType.CollectorStale,
                    dedupKey,
                    $"{source.Name} has not heartbeated since {last} (stale after {staleAfter})",
                    cancellationToken);
            }
            else
            {
                await alerts.ResolveAsync(dedupKey, cancellationToken);
            }
        }
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
