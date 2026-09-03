namespace Aegis.Collectors;

/// <summary>
/// A read-only adapter over one scheduler instance (SQL Agent, Airflow, later VisualCron).
/// A collector observes its scheduler and normalises what it sees into the AEGIS store. It
/// never writes to the scheduler: read-only first, not an orchestrator (design principles 1 and 6).
/// </summary>
public interface ICollector
{
    /// <summary>Scheduler kind. Stored in <c>SourceSystem.Type</c>, e.g. <c>SQLAgent</c> or <c>Airflow</c>.</summary>
    string SourceSystemType { get; }

    /// <summary>Human-readable instance name. Stored in <c>SourceSystem.Name</c>.</summary>
    string SourceSystemName { get; }

    /// <summary>
    /// Runs one complete poll cycle: heartbeat, read the source, persist the batch, record the
    /// sync outcome. Source and store failures never propagate; they come back as
    /// <see cref="CollectorResult.Error"/> so the host loop keeps running. The only exception that
    /// escapes is <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/>
    /// is cancelled (host shutdown).
    /// </summary>
    Task<CollectorResult> CollectAsync(CancellationToken cancellationToken = default);
}

/// <summary>Outcome of one <see cref="ICollector.CollectAsync"/> cycle.</summary>
public sealed record CollectorResult
{
    /// <summary>Jobs the source reported this cycle.</summary>
    public int JobCount { get; init; }

    /// <summary>Jobs with no owner that could be harvested from their metadata.</summary>
    public int UnownedCount { get; init; }

    /// <summary>Runs the source reported this cycle (new and already known alike).</summary>
    public int RunCount { get; init; }

    /// <summary>Null on success; otherwise the message that was recorded on the <c>CatalogSync</c> row.</summary>
    public string? Error { get; init; }

    public bool Success => string.IsNullOrEmpty(Error);

    public static CollectorResult Failed(string error) => new() { Error = error };
}

/// <summary>A job as its scheduler describes it, before it is bound to a canonical AEGIS <c>Job</c>.</summary>
public sealed record CollectedJob
{
    /// <summary>The scheduler's own stable identifier: SQL Agent <c>job_id</c>, Airflow <c>dag_id</c>.</summary>
    public required string NativeId { get; init; }

    /// <summary>The scheduler's display name for the job.</summary>
    public required string NativeName { get; init; }

    /// <summary>Free-text description; the primary source for ownership harvesting. Empty, never null.</summary>
    public required string Description { get; init; }

    /// <summary>Enabled in SQL Agent; unpaused and still present in Airflow.</summary>
    public bool IsActive { get; init; }

    /// <summary>
    /// An owner the scheduler declares as a first-class field (Airflow <c>owners</c>). When present it
    /// wins over anything parsed out of free text.
    /// </summary>
    public string? DeclaredOwner { get; init; }

    /// <summary>Scheduler-side tags, when the scheduler has them. A secondary ownership source.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    public DateTimeOffset? LastRunAt { get; init; }

    public DateTimeOffset? NextRunAt { get; init; }
}

/// <summary>One execution of a job, as the scheduler reports it.</summary>
public sealed record CollectedJobRun
{
    /// <summary>The <see cref="CollectedJob.NativeId"/> of the job this run belongs to.</summary>
    public required string NativeJobId { get; init; }

    /// <summary>
    /// The scheduler's identity for this execution, unique within the source system. Runs are
    /// deduplicated on it, and a run first seen in flight is later closed by the row that carries
    /// the same id with a terminal <see cref="Status"/>.
    /// </summary>
    public required string NativeRunId { get; init; }

    /// <summary>The scheduler's own start instant in UTC, never the collector's arrival time.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Null while the run is still open.</summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>One of the <see cref="RunStatus"/> values.</summary>
    public required string Status { get; init; }

    /// <summary>The failure message, when there is one. Fingerprinted for grouping; never contains row data.</summary>
    public string? ErrorText { get; init; }
}

/// <summary>
/// Everything one poll of a source produced, plus the watermark the source wants remembered.
/// The watermark is persisted in the same transaction as the jobs and runs, so a failed write
/// leaves it untouched and the next poll re-reads the same window.
/// </summary>
/// <param name="Watermark">Opaque to the store; each collector decides what it means. Null keeps the previous value.</param>
public sealed record CollectedBatch(
    IReadOnlyList<CollectedJob> Jobs,
    IReadOnlyList<CollectedJobRun> JobRuns,
    string? Watermark);
