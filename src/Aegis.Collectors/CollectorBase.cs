using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Aegis.Collectors;

/// <summary>
/// The poll cycle every collector shares. A subclass knows how to <em>read</em> one kind of
/// scheduler (<see cref="CollectDataAsync"/>); everything about the AEGIS store lives here so it
/// is implemented once: sync bookkeeping, canonical identity and source bindings, per-sync
/// ownership snapshots, run history, the source watermark, and the collector's own health alerts.
///
/// <para>One cycle: open a <c>CatalogSync</c> row, heartbeat, read the source, zero-row check,
/// then persist jobs, ownership, runs and the new watermark in <em>one transaction</em>, and
/// close the <c>CatalogSync</c> row. If anything fails the transaction rolls back, the watermark
/// stays where it was, and the next poll re-reads the same window; nothing is lost and nothing is
/// double-counted.</para>
///
/// <para>Runs are append-only in the sense that matters: a closed run is never touched again. A
/// run first seen in flight is <em>completed</em> in place when its terminal state arrives under
/// the same <see cref="CollectedJobRun.NativeRunId"/>. Without that, every run would either be
/// two rows or a row that says "Running" forever.</para>
/// </summary>
public abstract class CollectorBase : ICollector
{
    /// <summary>Trailing completed syncs that form the job-count baseline.</summary>
    private const int BaselineSyncCount = 5;

    /// <summary>Baseline samples needed before a sharp-drop alert may fire.</summary>
    private const int BaselineMinimumSamples = 3;

    /// <summary>A job count below this fraction of the baseline average is a sharp drop.</summary>
    private const double SharpDropFraction = 0.5;

    /// <summary>SQL Agent's placeholder when a job has no description; treated as empty evidence.</summary>
    private static readonly string[] EmptyDescriptions = ["No description available."];

    protected CollectorBase(string storeConnectionString, int sourceSystemId, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeConnectionString);
        ArgumentNullException.ThrowIfNull(logger);

        StoreConnectionString = storeConnectionString;
        SourceSystemId = sourceSystemId;
        Logger = logger;
    }

    /// <summary>Connection to the AEGIS store. Never the monitored scheduler; subclasses hold their own source connection.</summary>
    protected string StoreConnectionString { get; }

    /// <summary>The <c>SourceSystem.Id</c> this collector writes under.</summary>
    protected int SourceSystemId { get; }

    protected ILogger Logger { get; }

    public abstract string SourceSystemType { get; }

    public abstract string SourceSystemName { get; }

    /// <summary>
    /// Reads the source. <paramref name="watermark"/> is what the previous successful cycle returned
    /// in <see cref="CollectedBatch.Watermark"/>, or null on the first poll ever; only the
    /// implementation knows what it means. Throw for anything that should mark the sync as failed:
    /// an empty result must be an honest empty result, never a swallowed error.
    /// </summary>
    protected abstract Task<CollectedBatch> CollectDataAsync(string? watermark, CancellationToken cancellationToken);

    public async Task<CollectorResult> CollectAsync(CancellationToken cancellationToken = default)
    {
        long syncId;
        try
        {
            syncId = await StartCatalogSyncAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "Could not open a catalog sync for {SourceSystemName}; is the store reachable?", SourceSystemName);
            return CollectorResult.Failed($"Failed to start catalog sync: {ex.Message}");
        }

        try
        {
            await UpdateHeartbeatAsync(cancellationToken);

            var watermark = await GetWatermarkAsync(cancellationToken);
            var batch = await CollectDataAsync(watermark, cancellationToken);

            await CheckZeroRowAlertAsync(batch.Jobs.Count, cancellationToken);

            var unownedCount = await PersistBatchAsync(batch, syncId, cancellationToken);
            await CompleteCatalogSyncAsync(syncId, batch.Jobs.Count, unownedCount, SyncStatus.Completed, null, CancellationToken.None);

            return new CollectorResult
            {
                JobCount = batch.Jobs.Count,
                UnownedCount = unownedCount,
                RunCount = batch.JobRuns.Count,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown mid-cycle. Close the sync row so it does not read as "still running" forever.
            await TryCompleteCatalogSyncAsync(syncId, "Cancelled: host shutting down");
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Collection failed for {SourceSystemName}", SourceSystemName);
            await TryCompleteCatalogSyncAsync(syncId, ex.Message);
            return CollectorResult.Failed(ex.Message);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Sync bookkeeping and heartbeat
    // ---------------------------------------------------------------------------------------

    private async Task<long> StartCatalogSyncAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(StoreConnectionString);
        return await connection.ExecuteScalarAsync<long>(Command(@"
            INSERT INTO dbo.CatalogSync (SourceSystemId, StartedAt, JobCount, UnownedCount, Status)
            VALUES (@SourceSystemId, SYSUTCDATETIME(), 0, 0, @Status);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
            new { SourceSystemId, Status = SyncStatus.Running },
            cancellationToken));
    }

    private async Task CompleteCatalogSyncAsync(long syncId, int jobCount, int unownedCount, string status, string? error, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(StoreConnectionString);
        await connection.ExecuteAsync(Command(@"
            UPDATE dbo.CatalogSync
            SET CompletedAt = SYSUTCDATETIME(),
                JobCount = @JobCount,
                UnownedCount = @UnownedCount,
                Status = @Status,
                ErrorText = @ErrorText
            WHERE Id = @SyncId;",
            new { SyncId = syncId, JobCount = jobCount, UnownedCount = unownedCount, Status = status, ErrorText = error },
            cancellationToken));
    }

    /// <summary>Marks the sync failed without letting a second store failure mask the first.</summary>
    private async Task TryCompleteCatalogSyncAsync(long syncId, string error)
    {
        try
        {
            await CompleteCatalogSyncAsync(syncId, 0, 0, SyncStatus.Failed, error, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not mark catalog sync {SyncId} as failed for {SourceSystemName}", syncId, SourceSystemName);
        }
    }

    private async Task UpdateHeartbeatAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(StoreConnectionString);
        await connection.ExecuteAsync(Command(@"
            UPDATE dbo.SourceSystem
            SET LastHeartbeat = SYSUTCDATETIME(),
                UpdatedAt = SYSUTCDATETIME()
            WHERE Id = @SourceSystemId;",
            new { SourceSystemId },
            cancellationToken));
    }

    // ---------------------------------------------------------------------------------------
    // Watermark
    // ---------------------------------------------------------------------------------------

    private async Task<string?> GetWatermarkAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(StoreConnectionString);
        return await connection.ExecuteScalarAsync<string?>(Command(
            "SELECT Watermark FROM dbo.SourceSystem WHERE Id = @SourceSystemId;",
            new { SourceSystemId },
            cancellationToken));
    }

    private async Task SetWatermarkAsync(SqlConnection connection, SqlTransaction transaction, string watermark, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(Command(@"
            UPDATE dbo.SourceSystem
            SET Watermark = @Watermark,
                UpdatedAt = SYSUTCDATETIME()
            WHERE Id = @SourceSystemId;",
            new { Watermark = watermark, SourceSystemId },
            cancellationToken,
            transaction));
    }

    // ---------------------------------------------------------------------------------------
    // Persistence: one transaction per batch
    // ---------------------------------------------------------------------------------------

    private sealed record Binding(string JobId, int BindingId);

    private readonly record struct Ownership(string? TeamId, string ParsedFrom, string RawEvidence);

    /// <summary>
    /// Writes one batch atomically and returns how many jobs had no resolvable owner.
    /// Round trips: one to load the binding map, then two per job (upsert, ownership snapshot)
    /// and one per run. The map replaces the per-job and per-run lookups that would otherwise
    /// dominate a sync of a few thousand jobs.
    /// </summary>
    private async Task<int> PersistBatchAsync(CollectedBatch batch, long syncId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(StoreConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var bindings = await LoadActiveBindingsAsync(connection, transaction, cancellationToken);

        var unownedCount = 0;
        foreach (var job in batch.Jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var jobId = await UpsertJobAsync(connection, transaction, job, bindings, cancellationToken);
            var ownership = ResolveOwnership(job);
            if (ownership.TeamId is null)
            {
                unownedCount++;
            }

            await InsertOwnershipSnapshotAsync(connection, transaction, jobId, ownership, syncId, cancellationToken);
        }

        var inserted = 0;
        var closed = 0;
        var orphanJobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var run in batch.JobRuns)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!bindings.TryGetValue(run.NativeJobId, out var binding))
            {
                orphanJobs.Add(run.NativeJobId);
                continue;
            }

            switch (await UpsertJobRunAsync(connection, transaction, binding.JobId, run, cancellationToken))
            {
                case RunWrite.Inserted: inserted++; break;
                case RunWrite.Closed: closed++; break;
            }
        }

        if (orphanJobs.Count > 0)
        {
            Logger.LogWarning(
                "{SourceSystemName}: skipped runs for {Count} native job id(s) with no active binding (deleted jobs with surviving history?): {NativeJobIds}",
                SourceSystemName, orphanJobs.Count, string.Join(", ", orphanJobs.Take(10)));
        }

        if (batch.Watermark is not null)
        {
            await SetWatermarkAsync(connection, transaction, batch.Watermark, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        Logger.LogDebug(
            "{SourceSystemName}: persisted {JobCount} jobs ({UnownedCount} unowned), {Inserted} new runs, {Closed} runs closed, watermark {Watermark}",
            SourceSystemName, batch.Jobs.Count, unownedCount, inserted, closed, batch.Watermark ?? "(unchanged)");

        return unownedCount;
    }

    /// <summary>Native id to canonical job for every currently bound job of this source. Case-insensitive to match the store's collation.</summary>
    private async Task<Dictionary<string, Binding>> LoadActiveBindingsAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<(string NativeId, string JobId, int BindingId)>(Command(@"
            SELECT NativeId, JobId, Id AS BindingId
            FROM dbo.JobSourceBinding
            WHERE SourceSystemId = @SourceSystemId AND UnboundAt IS NULL;",
            new { SourceSystemId },
            cancellationToken,
            transaction));

        var map = new Dictionary<string, Binding>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            map[row.NativeId] = new Binding(row.JobId, row.BindingId);
        }

        return map;
    }

    /// <summary>
    /// Known native id: refresh the job's mutable fields and its binding's display name. Unknown:
    /// mint a canonical ULID and bind it. A native id is never fuzzy-matched to an existing job
    /// from another source; migration is a deliberate rebind (locked decision 1).
    /// </summary>
    private async Task<string> UpsertJobAsync(SqlConnection connection, SqlTransaction transaction, CollectedJob job, Dictionary<string, Binding> bindings, CancellationToken cancellationToken)
    {
        if (bindings.TryGetValue(job.NativeId, out var existing))
        {
            // BoundAt is deliberately left alone: it records when this binding began.
            await connection.ExecuteAsync(Command(@"
                UPDATE dbo.Job
                SET Name = @Name,
                    IsActive = @IsActive,
                    LastSeen = SYSUTCDATETIME(),
                    UpdatedAt = SYSUTCDATETIME()
                WHERE Id = @JobId;

                UPDATE dbo.JobSourceBinding
                SET NativeName = @Name
                WHERE Id = @BindingId AND (NativeName IS NULL OR NativeName <> @Name);",
                new { existing.JobId, existing.BindingId, Name = job.NativeName, job.IsActive },
                cancellationToken,
                transaction));

            return existing.JobId;
        }

        var newJobId = Ulid.NewUlid().ToString();
        var bindingId = await connection.ExecuteScalarAsync<int>(Command(@"
            INSERT INTO dbo.Job (Id, Name, IsActive, FirstSeen, LastSeen, CreatedAt, UpdatedAt)
            VALUES (@JobId, @Name, @IsActive, SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME());

            INSERT INTO dbo.JobSourceBinding (JobId, SourceSystemId, NativeId, NativeName, BoundAt, CreatedAt)
            VALUES (@JobId, @SourceSystemId, @NativeId, @Name, SYSUTCDATETIME(), SYSUTCDATETIME());

            SELECT CAST(SCOPE_IDENTITY() AS INT);",
            new { JobId = newJobId, Name = job.NativeName, job.IsActive, SourceSystemId, job.NativeId },
            cancellationToken,
            transaction));

        bindings[job.NativeId] = new Binding(newJobId, bindingId);
        Logger.LogInformation("{SourceSystemName}: new job {NativeName} bound as {JobId}", SourceSystemName, job.NativeName, newJobId);

        return newJobId;
    }

    /// <summary>
    /// Declared owner first, then the description, then tags. Each level records which field it
    /// came from and the exact text it read, so the decision can be audited and re-parsed later.
    /// </summary>
    private static Ownership ResolveOwnership(CollectedJob job)
    {
        if (!string.IsNullOrWhiteSpace(job.DeclaredOwner))
        {
            return new Ownership(job.DeclaredOwner.Trim(), OwnerSource.Declared, job.DeclaredOwner);
        }

        var description = EmptyDescriptions.Contains(job.Description.Trim(), StringComparer.OrdinalIgnoreCase)
            ? string.Empty
            : job.Description;

        if (OwnerParser.Parse(description) is { } fromDescription)
        {
            return new Ownership(
                fromDescription.Owner,
                fromDescription.IsTicket ? OwnerSource.Ticket : OwnerSource.Description,
                description);
        }

        foreach (var tag in job.Tags)
        {
            if (OwnerParser.Parse(tag) is { } fromTag)
            {
                return new Ownership(fromTag.Owner, OwnerSource.Tags, tag);
            }
        }

        return new Ownership(null, OwnerSource.None, description);
    }

    /// <summary>One row per job per sync, unowned or not (locked decision 3: snapshots, not mutation).</summary>
    private static async Task InsertOwnershipSnapshotAsync(SqlConnection connection, SqlTransaction transaction, string jobId, Ownership ownership, long syncId, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(Command(@"
            INSERT INTO dbo.JobOwnership (JobId, TeamId, RawEvidence, ParsedFrom, SyncId, CreatedAt)
            VALUES (@JobId, @TeamId, @RawEvidence, @ParsedFrom, @SyncId, SYSUTCDATETIME());",
            new { JobId = jobId, ownership.TeamId, ownership.RawEvidence, ownership.ParsedFrom, SyncId = syncId },
            cancellationToken,
            transaction));
    }

    private enum RunWrite
    {
        Unchanged = 0,
        Inserted = 1,
        Closed = 2,
    }

    /// <summary>
    /// New run: insert. Known run: only an open row may change, and only to a terminal state.
    /// A closed row is history and is never touched.
    /// </summary>
    private async Task<RunWrite> UpsertJobRunAsync(SqlConnection connection, SqlTransaction transaction, string jobId, CollectedJobRun run, CancellationToken cancellationToken)
    {
        var isTerminal = RunStatus.IsTerminal(run.Status);
        var fingerprint = run.Status == RunStatus.Failed && !string.IsNullOrWhiteSpace(run.ErrorText)
            ? ErrorFingerprint.Compute(run.ErrorText)
            : null;

        var outcome = await connection.ExecuteScalarAsync<int>(Command(@"
            DECLARE @Outcome INT = 0;

            IF NOT EXISTS (SELECT 1 FROM dbo.JobRun WHERE SourceSystemId = @SourceSystemId AND NativeRunId = @NativeRunId)
            BEGIN
                INSERT INTO dbo.JobRun (JobId, SourceSystemId, NativeRunId, StartedAt, EndedAt, Status, ErrorText, FingerprintId, CreatedAt)
                VALUES (@JobId, @SourceSystemId, @NativeRunId, @StartedAt, @EndedAt, @Status, @ErrorText, @FingerprintId, SYSUTCDATETIME());
                SET @Outcome = 1;
            END
            ELSE IF @IsTerminal = 1
            BEGIN
                UPDATE dbo.JobRun
                SET StartedAt = @StartedAt,
                    EndedAt = @EndedAt,
                    Status = @Status,
                    ErrorText = @ErrorText,
                    FingerprintId = @FingerprintId
                WHERE SourceSystemId = @SourceSystemId AND NativeRunId = @NativeRunId AND EndedAt IS NULL;
                IF @@ROWCOUNT > 0 SET @Outcome = 2;
            END

            SELECT @Outcome;",
            new
            {
                JobId = jobId,
                SourceSystemId,
                run.NativeRunId,
                run.StartedAt,
                run.EndedAt,
                run.Status,
                run.ErrorText,
                FingerprintId = fingerprint,
                IsTerminal = isTerminal,
            },
            cancellationToken,
            transaction));

        return (RunWrite)outcome;
    }

    // ---------------------------------------------------------------------------------------
    // Monitor the monitor
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// "Collector healthy, 0 jobs" is a contradiction, not a success: wrong msdb permissions and
    /// filtered API views both return empty sets with no error. Zero is always an alert; a count
    /// far below the trailing baseline is one too.
    /// </summary>
    private async Task CheckZeroRowAlertAsync(int jobCount, CancellationToken cancellationToken)
    {
        if (jobCount == 0)
        {
            await RaiseAlertAsync(
                AlertType.CollectorZeroRows,
                $"{SourceSystemName} returned 0 jobs: check permissions and configuration",
                cancellationToken);
            return;
        }

        await using var connection = new SqlConnection(StoreConnectionString);
        var recentCounts = (await connection.QueryAsync<int>(Command(@"
            SELECT TOP (@Take) JobCount
            FROM dbo.CatalogSync
            WHERE SourceSystemId = @SourceSystemId
              AND Status = @Completed
              AND JobCount > 0
            ORDER BY StartedAt DESC;",
            new { Take = BaselineSyncCount, SourceSystemId, Completed = SyncStatus.Completed },
            cancellationToken))).ToList();

        if (recentCounts.Count < BaselineMinimumSamples)
        {
            return;
        }

        var average = recentCounts.Average();
        if (jobCount < average * SharpDropFraction)
        {
            await RaiseAlertAsync(
                AlertType.CollectorSharpDrop,
                $"{SourceSystemName} job count dropped from a baseline of {average:F0} to {jobCount}",
                cancellationToken);
        }
    }

    /// <summary>
    /// Raises or re-fires an alert keyed on (type, source). Re-firing bumps <c>Occurrences</c>
    /// and <c>LastOccurrence</c>; nothing here resolves alerts, that is roadmap task 2.5.
    /// The message is logged only: the <c>Alert</c> table has no text column yet.
    /// </summary>
    protected async Task RaiseAlertAsync(string type, string message, CancellationToken cancellationToken)
    {
        Logger.LogWarning("Alert {AlertType} for {SourceSystemName}: {Message}", type, SourceSystemName, message);

        var dedupKey = ErrorFingerprint.DedupKey(type, SourceSystemId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        await using var connection = new SqlConnection(StoreConnectionString);
        await connection.ExecuteAsync(Command(@"
            MERGE dbo.Alert WITH (HOLDLOCK) AS target
            USING (SELECT @DedupKey AS DedupKey) AS source
                ON target.DedupKey = source.DedupKey
            WHEN MATCHED THEN
                UPDATE SET LastOccurrence = SYSUTCDATETIME(),
                           Occurrences = target.Occurrences + 1,
                           UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (Type, DedupKey, Status, RoutedTo, FirstSeen, LastOccurrence, Occurrences, CreatedAt, UpdatedAt)
                VALUES (@Type, @DedupKey, 'Firing', NULL, SYSUTCDATETIME(), SYSUTCDATETIME(), 1, SYSUTCDATETIME(), SYSUTCDATETIME());",
            new { Type = type, DedupKey = dedupKey },
            cancellationToken));
    }

    /// <summary>Dapper command with cancellation wired through; every store call goes through here.</summary>
    private static CommandDefinition Command(string sql, object parameters, CancellationToken cancellationToken, IDbTransaction? transaction = null)
        => new(sql, parameters, transaction, cancellationToken: cancellationToken);
}
