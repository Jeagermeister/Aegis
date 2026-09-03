using Aegis.Collectors;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aegis.IntegrationTests;

/// <summary>
/// The shared persistence cycle against a real store: identity, bindings, ownership snapshots,
/// run history, watermark, and the collector's own alerts. Roadmap 1.2 ("round-trip tests")
/// and 1.3 ("point a collector at an empty source, get an alert").
/// </summary>
public sealed class CollectorRoundTripTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private static readonly DateTimeOffset Start = new(2026, 9, 3, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task First_sync_binds_jobs_snapshots_ownership_stores_runs_and_the_watermark()
    {
        var source = await NewSourceSystemAsync();
        var collector = new FakeCollector(fixture.ConnectionString, source, _ => new CollectedBatch(
            [
                Job("job-a", "Nightly load", "Owner: ETL; Ticket: DE-1"),
                Job("job-b", "Claims load", "See wiki."),
            ],
            [
                Run("job-a", "run-1", RunStatus.Failed, Start, Start.AddMinutes(3), "Could not find file '/landing/x_20260903.csv'"),
                Run("job-b", "run-2", RunStatus.Running, Start.AddMinutes(1), null, null),
            ],
            Watermark: "42"));

        var result = await collector.CollectAsync();

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.JobCount);
        Assert.Equal(1, result.UnownedCount);
        Assert.Equal(2, result.RunCount);

        await using var db = new SqlConnection(fixture.ConnectionString);

        Assert.Equal("42", await db.ExecuteScalarAsync<string>("SELECT Watermark FROM dbo.SourceSystem WHERE Id = @source", new { source }));
        Assert.NotNull(await db.ExecuteScalarAsync<DateTime?>("SELECT LastHeartbeat FROM dbo.SourceSystem WHERE Id = @source", new { source }));

        var bindings = (await db.QueryAsync<(string NativeId, string JobId)>(
            "SELECT NativeId, JobId FROM dbo.JobSourceBinding WHERE SourceSystemId = @source AND UnboundAt IS NULL", new { source })).ToList();
        Assert.Equal(2, bindings.Count);
        Assert.All(bindings, binding => Assert.Equal(26, binding.JobId.Length));

        var ownership = (await db.QueryAsync<(string NativeId, string? TeamId, string ParsedFrom)>(@"
            SELECT b.NativeId, o.TeamId, o.ParsedFrom
            FROM dbo.JobOwnership o
            JOIN dbo.JobSourceBinding b ON b.JobId = o.JobId
            WHERE b.SourceSystemId = @source", new { source })).ToDictionary(row => row.NativeId);
        Assert.Equal(("ETL", OwnerSource.Description), (ownership["job-a"].TeamId, ownership["job-a"].ParsedFrom));
        Assert.Equal((null, OwnerSource.None), (ownership["job-b"].TeamId, ownership["job-b"].ParsedFrom));

        var runs = (await db.QueryAsync<(string NativeRunId, string Status, DateTime? EndedAt, string? FingerprintId)>(
            "SELECT NativeRunId, Status, EndedAt, FingerprintId FROM dbo.JobRun WHERE SourceSystemId = @source", new { source })).ToDictionary(row => row.NativeRunId);
        Assert.Equal(RunStatus.Failed, runs["run-1"].Status);
        Assert.Equal(32, runs["run-1"].FingerprintId?.Length);
        Assert.Equal(RunStatus.Running, runs["run-2"].Status);
        Assert.Null(runs["run-2"].EndedAt);
        Assert.Null(runs["run-2"].FingerprintId);

        var sync = await db.QuerySingleAsync<(string Status, int JobCount, int UnownedCount, DateTime? CompletedAt)>(
            "SELECT Status, JobCount, UnownedCount, CompletedAt FROM dbo.CatalogSync WHERE SourceSystemId = @source", new { source });
        Assert.Equal((SyncStatus.Completed, 2, 1), (sync.Status, sync.JobCount, sync.UnownedCount));
        Assert.NotNull(sync.CompletedAt);
    }

    [Fact]
    public async Task Second_sync_reuses_identity_keeps_bound_at_snapshots_ownership_again_and_closes_the_open_run()
    {
        var source = await NewSourceSystemAsync();
        string? watermarkSeen = null;
        var status = RunStatus.Running;
        DateTimeOffset? endedAt = null;

        var collector = new FakeCollector(fixture.ConnectionString, source, watermark =>
        {
            watermarkSeen = watermark;
            return new CollectedBatch(
                [Job("job-a", "Nightly load", "Owner: ETL")],
                [Run("job-a", "run-1", status, Start, endedAt, null)],
                Watermark: watermark is null ? "1" : "2");
        });

        Assert.True((await collector.CollectAsync()).Success);
        await using var db = new SqlConnection(fixture.ConnectionString);
        var firstBoundAt = await db.ExecuteScalarAsync<DateTime>("SELECT BoundAt FROM dbo.JobSourceBinding WHERE SourceSystemId = @source", new { source });
        var firstLastSeen = await db.ExecuteScalarAsync<DateTime>("SELECT j.LastSeen FROM dbo.Job j JOIN dbo.JobSourceBinding b ON b.JobId = j.Id WHERE b.SourceSystemId = @source", new { source });

        await Task.Delay(50);
        status = RunStatus.Succeeded;
        endedAt = Start.AddMinutes(5);
        Assert.True((await collector.CollectAsync()).Success);

        Assert.Equal("1", watermarkSeen);
        Assert.Equal("2", await db.ExecuteScalarAsync<string>("SELECT Watermark FROM dbo.SourceSystem WHERE Id = @source", new { source }));

        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.JobSourceBinding WHERE SourceSystemId = @source", new { source }));
        Assert.Equal(firstBoundAt, await db.ExecuteScalarAsync<DateTime>("SELECT BoundAt FROM dbo.JobSourceBinding WHERE SourceSystemId = @source", new { source }));
        Assert.True(await db.ExecuteScalarAsync<DateTime>("SELECT j.LastSeen FROM dbo.Job j JOIN dbo.JobSourceBinding b ON b.JobId = j.Id WHERE b.SourceSystemId = @source", new { source }) > firstLastSeen);

        Assert.Equal(2, await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.JobOwnership o JOIN dbo.JobSourceBinding b ON b.JobId = o.JobId WHERE b.SourceSystemId = @source", new { source }));

        var run = await db.QuerySingleAsync<(string Status, DateTime? EndedAt)>(
            "SELECT Status, EndedAt FROM dbo.JobRun WHERE SourceSystemId = @source AND NativeRunId = 'run-1'", new { source });
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(endedAt.Value.UtcDateTime, run.EndedAt);
    }

    [Fact]
    public async Task A_closed_run_is_never_rewritten()
    {
        var source = await NewSourceSystemAsync();
        var status = RunStatus.Succeeded;
        var collector = new FakeCollector(fixture.ConnectionString, source, _ => new CollectedBatch(
            [Job("job-a", "Nightly load", "")],
            [Run("job-a", "run-1", status, Start, Start.AddMinutes(1), status == RunStatus.Failed ? "boom" : null)],
            Watermark: null));

        Assert.True((await collector.CollectAsync()).Success);
        status = RunStatus.Failed;
        Assert.True((await collector.CollectAsync()).Success);

        await using var db = new SqlConnection(fixture.ConnectionString);
        Assert.Equal(RunStatus.Succeeded, await db.ExecuteScalarAsync<string>(
            "SELECT Status FROM dbo.JobRun WHERE SourceSystemId = @source AND NativeRunId = 'run-1'", new { source }));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.JobRun WHERE SourceSystemId = @source", new { source }));
    }

    [Fact]
    public async Task An_empty_source_is_an_alert_and_a_throwing_source_is_a_failed_sync_that_keeps_the_watermark()
    {
        var source = await NewSourceSystemAsync();
        var throwNext = false;
        var collector = new FakeCollector(fixture.ConnectionString, source, watermark =>
        {
            if (throwNext)
            {
                throw new InvalidOperationException("msdb said no");
            }

            return new CollectedBatch([], [], Watermark: "7");
        });

        Assert.True((await collector.CollectAsync()).Success);
        Assert.True((await collector.CollectAsync()).Success);

        await using var db = new SqlConnection(fixture.ConnectionString);
        var dedupKey = ErrorFingerprint.DedupKey(AlertType.CollectorZeroRows, source.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var alert = await db.QuerySingleAsync<(string Type, string Status, int Occurrences)>(
            "SELECT Type, Status, Occurrences FROM dbo.Alert WHERE DedupKey = @dedupKey", new { dedupKey });
        Assert.Equal((AlertType.CollectorZeroRows, "Firing", 2), alert);

        throwNext = true;
        var failed = await collector.CollectAsync();

        Assert.False(failed.Success);
        Assert.Equal("msdb said no", failed.Error);
        Assert.Equal("7", await db.ExecuteScalarAsync<string>("SELECT Watermark FROM dbo.SourceSystem WHERE Id = @source", new { source }));

        var lastSync = await db.QuerySingleAsync<(string Status, string? ErrorText)>(
            "SELECT TOP 1 Status, ErrorText FROM dbo.CatalogSync WHERE SourceSystemId = @source ORDER BY Id DESC", new { source });
        Assert.Equal((SyncStatus.Failed, "msdb said no"), lastSync);
    }

    [Fact]
    public async Task A_run_for_an_unknown_job_is_skipped_not_fatal()
    {
        var source = await NewSourceSystemAsync();
        var collector = new FakeCollector(fixture.ConnectionString, source, _ => new CollectedBatch(
            [Job("job-a", "Nightly load", "")],
            [Run("deleted-job", "run-9", RunStatus.Succeeded, Start, Start.AddMinutes(1), null)],
            Watermark: null));

        var result = await collector.CollectAsync();

        Assert.True(result.Success, result.Error);
        await using var db = new SqlConnection(fixture.ConnectionString);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.JobRun WHERE SourceSystemId = @source", new { source }));
    }

    // ---------------------------------------------------------------------------------------

    private async Task<int> NewSourceSystemAsync()
    {
        await using var db = new SqlConnection(fixture.ConnectionString);
        return await db.ExecuteScalarAsync<int>(@"
            INSERT INTO dbo.SourceSystem (Type, Name, Config) VALUES ('Fake', @name, '{}');
            SELECT CAST(SCOPE_IDENTITY() AS INT);",
            new { name = Guid.NewGuid().ToString("N") });
    }

    private static CollectedJob Job(string nativeId, string name, string description) => new()
    {
        NativeId = nativeId,
        NativeName = name,
        Description = description,
        IsActive = true,
    };

    private static CollectedJobRun Run(string nativeJobId, string nativeRunId, string status, DateTimeOffset startedAt, DateTimeOffset? endedAt, string? error) => new()
    {
        NativeJobId = nativeJobId,
        NativeRunId = nativeRunId,
        StartedAt = startedAt,
        EndedAt = endedAt,
        Status = status,
        ErrorText = error,
    };

    /// <summary>A collector with no scheduler behind it: the batch is whatever the test says it is.</summary>
    private sealed class FakeCollector(string store, int sourceSystemId, Func<string?, CollectedBatch> produce)
        : CollectorBase(store, sourceSystemId, NullLogger.Instance)
    {
        public override string SourceSystemType => "Fake";

        public override string SourceSystemName => "fake";

        protected override Task<CollectedBatch> CollectDataAsync(string? watermark, CancellationToken cancellationToken)
            => Task.FromResult(produce(watermark));
    }
}
