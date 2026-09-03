using Aegis.Collectors;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aegis.IntegrationTests;

/// <summary>
/// The stale-collector sweep (roadmap 1.3): a source whose heartbeat goes quiet raises a
/// <c>CollectorStale</c> alert, and a returning heartbeat resolves it.
/// </summary>
public sealed class StaleCollectorSweepTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    [Fact]
    public async Task A_source_with_a_stale_heartbeat_raises_a_collector_stale_alert()
    {
        var source = await NewSourceSystemAsync(heartbeat: DateTime.UtcNow.AddMinutes(-10));

        await SweepAsync(staleAfter: TimeSpan.FromMinutes(5));

        await using var db = new SqlConnection(fixture.ConnectionString);
        var dedupKey = ErrorFingerprint.DedupKey(AlertType.CollectorStale, source.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var alert = await db.QuerySingleAsync<(string Type, string Status, int Occurrences)>(
            "SELECT Type, Status, Occurrences FROM dbo.Alert WHERE DedupKey = @dedupKey", new { dedupKey });
        Assert.Equal((AlertType.CollectorStale, "Firing", 1), alert);
    }

    [Fact]
    public async Task A_source_that_has_never_heartbeated_is_stale()
    {
        var source = await NewSourceSystemAsync(heartbeat: null);

        await SweepAsync(staleAfter: TimeSpan.FromMinutes(5));

        await using var db = new SqlConnection(fixture.ConnectionString);
        var dedupKey = ErrorFingerprint.DedupKey(AlertType.CollectorStale, source.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("Firing", await db.ExecuteScalarAsync<string>(
            "SELECT Status FROM dbo.Alert WHERE DedupKey = @dedupKey", new { dedupKey }));
    }

    [Fact]
    public async Task A_fresh_heartbeat_resolves_a_firing_stale_alert()
    {
        var source = await NewSourceSystemAsync(heartbeat: DateTime.UtcNow.AddMinutes(-10));

        await SweepAsync(staleAfter: TimeSpan.FromMinutes(5));
        await using (var db = new SqlConnection(fixture.ConnectionString))
        {
            await db.ExecuteAsync(
                "UPDATE dbo.SourceSystem SET LastHeartbeat = SYSUTCDATETIME() WHERE Id = @source", new { source });
        }

        await SweepAsync(staleAfter: TimeSpan.FromMinutes(5));

        await using var db2 = new SqlConnection(fixture.ConnectionString);
        var dedupKey = ErrorFingerprint.DedupKey(AlertType.CollectorStale, source.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("Resolved", await db2.ExecuteScalarAsync<string>(
            "SELECT Status FROM dbo.Alert WHERE DedupKey = @dedupKey", new { dedupKey }));
    }

    [Fact]
    public async Task A_fresh_heartbeat_does_not_raise_an_alert()
    {
        var source = await NewSourceSystemAsync(heartbeat: DateTime.UtcNow);

        await SweepAsync(staleAfter: TimeSpan.FromMinutes(5));

        await using var db = new SqlConnection(fixture.ConnectionString);
        var dedupKey = ErrorFingerprint.DedupKey(AlertType.CollectorStale, source.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Null(await db.ExecuteScalarAsync<string?>(
            "SELECT Status FROM dbo.Alert WHERE DedupKey = @dedupKey", new { dedupKey }));
    }

    // ---------------------------------------------------------------------------------------

    private async Task SweepAsync(TimeSpan staleAfter)
    {
        var options = Options.Create(new CollectorOptions { StoreConnectionString = fixture.ConnectionString });
        var service = new StaleCollectorSweepService(options, NullLogger<StaleCollectorSweepService>.Instance);
        var alerts = new AlertStore(fixture.ConnectionString, NullLogger.Instance);
        await service.SweepAsync(alerts, staleAfter, CancellationToken.None);
    }

    private async Task<int> NewSourceSystemAsync(DateTime? heartbeat)
    {
        await using var db = new SqlConnection(fixture.ConnectionString);
        return await db.ExecuteScalarAsync<int>(@"
            INSERT INTO dbo.SourceSystem (Type, Name, Config, LastHeartbeat) VALUES ('Fake', @name, '{}', @heartbeat);
            SELECT CAST(SCOPE_IDENTITY() AS INT);",
            new { name = Guid.NewGuid().ToString("N"), heartbeat });
    }
}
