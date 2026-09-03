using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aegis.Collectors;

/// <summary>
/// Hosts every configured collector, each on its own <see cref="PeriodicTimer"/>, in one process
/// (TECH-STACK: plain <c>BackgroundService</c>, no job-queue framework). Startup registers each
/// source in <c>SourceSystem</c> and waits for the store if it is not up yet; a misconfigured
/// source, by contrast, fails the host immediately, because a typo that idles quietly is the
/// blind spot the whole design is built to avoid.
/// </summary>
public sealed class CollectorHostedService : BackgroundService
{
    private static readonly TimeSpan StoreRetryDelay = TimeSpan.FromSeconds(15);

    private readonly ILogger<CollectorHostedService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly CollectorOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;

    public CollectorHostedService(
        IOptions<CollectorOptions> options,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<CollectorHostedService>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configured = _options.SqlAgentCollectors.Count + _options.AirflowCollectors.Count;
        if (configured == 0)
        {
            _logger.LogWarning("No collectors configured under '{Section}'; nothing to poll", CollectorOptions.SectionName);
            return;
        }

        ValidateOptions();

        List<ICollector> collectors;
        try
        {
            collectors = await InitializeCollectorsAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.PollIntervalSeconds));
        _logger.LogInformation("Polling {Count} source(s) every {Interval}", collectors.Count, interval);

        await Task.WhenAll(collectors.Select(collector => RunCollectorLoopAsync(collector, interval, stoppingToken)));

        _logger.LogInformation("Collector host stopped");
    }

    private async Task RunCollectorLoopAsync(ICollector collector, TimeSpan interval, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                var result = await collector.CollectAsync(stoppingToken);
                if (result.Success)
                {
                    _logger.LogInformation(
                        "{SourceSystemType} {SourceSystemName}: {JobCount} jobs, {UnownedCount} unowned, {RunCount} runs",
                        collector.SourceSystemType, collector.SourceSystemName, result.JobCount, result.UnownedCount, result.RunCount);
                }
                else
                {
                    _logger.LogError(
                        "{SourceSystemType} {SourceSystemName}: sync failed: {Error}",
                        collector.SourceSystemType, collector.SourceSystemName, result.Error);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // CollectAsync is not supposed to let anything else out; if it does, the loop must survive it.
                _logger.LogError(ex, "{SourceSystemName}: unexpected error in the poll loop", collector.SourceSystemName);
            }
        }
        while (await WaitForNextTickAsync(timer, stoppingToken));
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

    // ---------------------------------------------------------------------------------------
    // Startup
    // ---------------------------------------------------------------------------------------

    /// <summary>Configuration mistakes fail fast, before any store access.</summary>
    private void ValidateOptions()
    {
        if (string.IsNullOrWhiteSpace(_options.StoreConnectionString))
        {
            throw new InvalidOperationException("No store connection string: set ConnectionStrings:Aegis or Collectors:StoreConnectionString");
        }

        foreach (var sqlAgent in _options.SqlAgentCollectors)
        {
            Require(sqlAgent.InstanceName, "Collectors:SqlAgentCollectors:*:InstanceName");
            Require(sqlAgent.ConnectionString, $"Collectors:SqlAgentCollectors ({sqlAgent.InstanceName}) ConnectionString");
            _ = TimeZoneInfo.FindSystemTimeZoneById(sqlAgent.TimeZoneId);
        }

        foreach (var airflow in _options.AirflowCollectors)
        {
            Require(airflow.InstanceName, "Collectors:AirflowCollectors:*:InstanceName");
            Require(airflow.BaseUrl, $"Collectors:AirflowCollectors ({airflow.InstanceName}) BaseUrl");
            if (!Uri.TryCreate(airflow.BaseUrl, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException($"Collectors:AirflowCollectors ({airflow.InstanceName}) BaseUrl is not an absolute URL: {airflow.BaseUrl}");
            }
        }

        static void Require(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"Missing required configuration value: {name}");
            }
        }
    }

    private async Task<List<ICollector>> InitializeCollectorsAsync(CancellationToken cancellationToken)
    {
        var collectors = new List<ICollector>();

        foreach (var sqlAgent in _options.SqlAgentCollectors)
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(sqlAgent.TimeZoneId);
            var descriptor = DescribeSqlSource(sqlAgent.ConnectionString);
            var id = await EnsureSourceSystemAsync(SqlAgentCollector.Type, sqlAgent.InstanceName, descriptor, cancellationToken);

            collectors.Add(new SqlAgentCollector(
                _options.StoreConnectionString,
                sqlAgent.ConnectionString,
                id,
                sqlAgent.InstanceName,
                zone,
                _loggerFactory.CreateLogger<SqlAgentCollector>()));

            _logger.LogInformation("SQL Agent source {InstanceName} registered as SourceSystem {Id} ({Descriptor}, zone {Zone})",
                sqlAgent.InstanceName, id, descriptor, zone.Id);
        }

        foreach (var airflow in _options.AirflowCollectors)
        {
            var descriptor = JsonSerializer.Serialize(new { airflow.BaseUrl, airflow.Username });
            var id = await EnsureSourceSystemAsync(AirflowCollector.Type, airflow.InstanceName, descriptor, cancellationToken);

            var http = _httpClientFactory.CreateClient($"airflow:{airflow.InstanceName}");
            AirflowCollector.ConfigureClient(http, airflow.BaseUrl, airflow.Username, airflow.Password);

            collectors.Add(new AirflowCollector(
                _options.StoreConnectionString,
                id,
                http,
                airflow.InstanceName,
                TimeSpan.FromDays(Math.Max(0, airflow.InitialLookbackDays)),
                _loggerFactory.CreateLogger<AirflowCollector>()));

            _logger.LogInformation("Airflow source {InstanceName} registered as SourceSystem {Id} ({Descriptor})",
                airflow.InstanceName, id, descriptor);
        }

        return collectors;
    }

    /// <summary>What we are allowed to remember about a SQL source: where it is, never how we log in.</summary>
    private static string DescribeSqlSource(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        return JsonSerializer.Serialize(new { Server = builder.DataSource, Database = builder.InitialCatalog });
    }

    /// <summary>
    /// Finds or creates the <c>SourceSystem</c> row for (type, name). Retries while the store is
    /// unreachable: on a dev box the containers are often still starting when the host comes up.
    /// </summary>
    private async Task<int> EnsureSourceSystemAsync(string type, string name, string config, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await using var connection = new SqlConnection(_options.StoreConnectionString);
                await connection.OpenAsync(cancellationToken);

                var existingId = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                    "SELECT Id FROM dbo.SourceSystem WHERE Type = @Type AND Name = @Name;",
                    new { Type = type, Name = name },
                    cancellationToken: cancellationToken));

                if (existingId is { } id)
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        "UPDATE dbo.SourceSystem SET Config = @Config, UpdatedAt = SYSUTCDATETIME() WHERE Id = @Id;",
                        new { Id = id, Config = config },
                        cancellationToken: cancellationToken));
                    return id;
                }

                return await connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
                    INSERT INTO dbo.SourceSystem (Type, Name, Config, CreatedAt, UpdatedAt)
                    VALUES (@Type, @Name, @Config, SYSUTCDATETIME(), SYSUTCDATETIME());
                    SELECT CAST(SCOPE_IDENTITY() AS INT);",
                    new { Type = type, Name = name, Config = config },
                    cancellationToken: cancellationToken));
            }
            catch (SqlException ex)
            {
                _logger.LogWarning(ex, "Store not reachable while registering {Type} {Name}; retrying in {Delay}", type, name, StoreRetryDelay);
                await Task.Delay(StoreRetryDelay, cancellationToken);
            }
        }
    }
}
