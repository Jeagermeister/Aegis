using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Aegis.Collectors;

/// <summary>
/// Reads SQL Server Agent through <c>msdb</c> with plain SELECTs: no Agent procedures called,
/// nothing written. Needs read access to msdb and nothing else.
///
/// <para><b>Execution identity.</b> SQL Agent has no execution id. <c>sysjobhistory</c> holds one
/// row per completed step plus one job-outcome row (<c>step_id = 0</c>) per execution;
/// <c>sysjobactivity</c> holds each job's <em>current</em> activity for the Agent session. Both
/// identify an execution by (job, start time), so that is the <see cref="CollectedJobRun.NativeRunId"/>
/// here (<see cref="MsdbTime.ExecutionKey"/>). An in-flight run inserted from <c>sysjobactivity</c>
/// is closed later by the outcome row from <c>sysjobhistory</c> carrying the same key. Step rows
/// are not runs; the failed step's message becomes the run's <c>ErrorText</c>, because it holds
/// the actual error where the outcome row only says which step failed.</para>
///
/// <para><b>Watermark.</b> <c>instance_id</c> is monotonic, so the watermark is the highest
/// outcome-row <c>instance_id</c> persisted so far. Polling above it is incremental, and
/// <c>MIN(instance_id)</c> jumping past it means history was purged underneath us (DESIGN-v2:
/// "sysjobhistory retention is the trap"). The watermark stops at the last <em>outcome</em> row, not
/// the last row, so the step rows of an execution still in progress are re-read together with their
/// outcome next time.</para>
/// </summary>
public sealed class SqlAgentCollector : CollectorBase
{
    public const string Type = "SQLAgent";

    private readonly string _sourceConnectionString;
    private readonly string _instanceName;
    private readonly TimeZoneInfo _sourceZone;

    /// <param name="storeConnectionString">The AEGIS store.</param>
    /// <param name="sourceConnectionString">The monitored instance; only msdb is read.</param>
    /// <param name="sourceZone">The monitored instance's time zone. msdb stores local wall-clock time.</param>
    public SqlAgentCollector(
        string storeConnectionString,
        string sourceConnectionString,
        int sourceSystemId,
        string instanceName,
        TimeZoneInfo sourceZone,
        ILogger logger)
        : base(storeConnectionString, sourceSystemId, logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        ArgumentNullException.ThrowIfNull(sourceZone);

        _sourceConnectionString = sourceConnectionString;
        _instanceName = instanceName;
        _sourceZone = sourceZone;
    }

    public override string SourceSystemType => Type;

    public override string SourceSystemName => _instanceName;

    protected override async Task<CollectedBatch> CollectDataAsync(string? watermark, CancellationToken cancellationToken)
    {
        var since = long.TryParse(watermark, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0L;

        await using var msdb = new SqlConnection(_sourceConnectionString);
        await msdb.OpenAsync(cancellationToken);

        var jobs = await CollectJobsAsync(msdb, cancellationToken);
        var (runs, lastOutcomeInstanceId) = await CollectCompletedRunsAsync(msdb, since, cancellationToken);
        runs.AddRange(await CollectInFlightRunsAsync(msdb, cancellationToken));
        await CheckHistoryPurgeGapAsync(msdb, since, cancellationToken);

        var newWatermark = lastOutcomeInstanceId is { } id && id > since
            ? id.ToString(CultureInfo.InvariantCulture)
            : watermark;

        Logger.LogDebug("{InstanceName}: {JobCount} jobs, {RunCount} runs, watermark {Since} -> {Watermark}",
            _instanceName, jobs.Count, runs.Count, since, newWatermark ?? "(none)");

        return new CollectedBatch(jobs, runs, newWatermark);
    }

    // ---------------------------------------------------------------------------------------
    // Jobs
    // ---------------------------------------------------------------------------------------

    private sealed class JobRow
    {
        public Guid JobId { get; set; }
        public string Name { get; set; } = string.Empty;
        public byte Enabled { get; set; }
        public string? Description { get; set; }
        public int? LastRunDate { get; set; }
        public int? LastRunTime { get; set; }
        public DateTime? NextScheduledRunDate { get; set; }
    }

    /// <summary>
    /// <c>sysjobservers</c> (server_id 0 = this instance) carries the packed last-run pair;
    /// <c>sysjobactivity</c> for the current Agent session carries the next scheduled run as a real
    /// DATETIME. Neither is on <c>sysjobs</c> itself.
    /// </summary>
    private async Task<List<CollectedJob>> CollectJobsAsync(SqlConnection msdb, CancellationToken cancellationToken)
    {
        var rows = await msdb.QueryAsync<JobRow>(new CommandDefinition(@"
            SELECT
                j.job_id                    AS JobId,
                j.name                      AS Name,
                j.enabled                   AS Enabled,
                j.description               AS Description,
                js.last_run_date            AS LastRunDate,
                js.last_run_time            AS LastRunTime,
                ja.next_scheduled_run_date  AS NextScheduledRunDate
            FROM msdb.dbo.sysjobs AS j
            LEFT JOIN msdb.dbo.sysjobservers AS js
                   ON js.job_id = j.job_id AND js.server_id = 0
            LEFT JOIN msdb.dbo.sysjobactivity AS ja
                   ON ja.job_id = j.job_id
                  AND ja.session_id = (SELECT MAX(session_id) FROM msdb.dbo.syssessions)
            ORDER BY j.name;",
            cancellationToken: cancellationToken));

        var jobs = new List<CollectedJob>();
        foreach (var row in rows)
        {
            jobs.Add(new CollectedJob
            {
                NativeId = row.JobId.ToString("D"),
                NativeName = row.Name,
                Description = row.Description ?? string.Empty,
                IsActive = row.Enabled == 1,
                LastRunAt = row.LastRunDate is { } date && row.LastRunTime is { } time
                    ? MsdbTime.ToUtc(date, time, _sourceZone)
                    : null,
                NextRunAt = MsdbTime.ToUtc(row.NextScheduledRunDate, _sourceZone),
            });
        }

        return jobs;
    }

    // ---------------------------------------------------------------------------------------
    // Completed runs from sysjobhistory
    // ---------------------------------------------------------------------------------------

    private sealed class HistoryRow
    {
        public long InstanceId { get; set; }
        public Guid JobId { get; set; }
        public int StepId { get; set; }
        public string? StepName { get; set; }
        public int RunStatus { get; set; }
        public int RunDate { get; set; }
        public int RunTime { get; set; }
        public int RunDuration { get; set; }
        public string? Message { get; set; }
    }

    /// <summary>
    /// Reads every history row above the watermark in <c>instance_id</c> order and folds them into
    /// executions. Step rows are logged as they finish and the outcome row (<c>step_id = 0</c>) last,
    /// so steps accumulate per job until their outcome arrives. Returns the runs and the
    /// <c>instance_id</c> of the last outcome row seen, which becomes the new watermark.
    /// </summary>
    private async Task<(List<CollectedJobRun> Runs, long? LastOutcomeInstanceId)> CollectCompletedRunsAsync(SqlConnection msdb, long since, CancellationToken cancellationToken)
    {
        var rows = await msdb.QueryAsync<HistoryRow>(new CommandDefinition(@"
            SELECT
                h.instance_id   AS InstanceId,
                h.job_id        AS JobId,
                h.step_id       AS StepId,
                h.step_name     AS StepName,
                h.run_status    AS RunStatus,
                h.run_date      AS RunDate,
                h.run_time      AS RunTime,
                h.run_duration  AS RunDuration,
                h.message       AS Message
            FROM msdb.dbo.sysjobhistory AS h
            WHERE h.instance_id > @Since
            ORDER BY h.instance_id;",
            new { Since = since },
            cancellationToken: cancellationToken));

        var runs = new List<CollectedJobRun>();
        var pendingSteps = new Dictionary<Guid, List<HistoryRow>>();
        long? lastOutcomeInstanceId = null;

        foreach (var row in rows)
        {
            if (row.StepId != 0)
            {
                if (!pendingSteps.TryGetValue(row.JobId, out var steps))
                {
                    pendingSteps[row.JobId] = steps = [];
                }

                steps.Add(row);
                continue;
            }

            lastOutcomeInstanceId = row.InstanceId;

            var startedAt = MsdbTime.ToUtc(row.RunDate, row.RunTime, _sourceZone);
            if (startedAt is null)
            {
                continue;
            }

            var status = MapRunStatus(row.RunStatus);
            string? errorText = null;
            if (status == Collectors.RunStatus.Failed)
            {
                // The failed step carries the real error; the outcome row only names the step.
                var failedStep = pendingSteps.TryGetValue(row.JobId, out var steps)
                    ? steps.LastOrDefault(s => s.RunStatus == 0)
                    : null;
                errorText = failedStep?.Message ?? row.Message;
            }

            pendingSteps.Remove(row.JobId);

            runs.Add(new CollectedJobRun
            {
                NativeJobId = row.JobId.ToString("D"),
                NativeRunId = MsdbTime.ExecutionKey(row.JobId, startedAt.Value),
                StartedAt = startedAt.Value,
                EndedAt = startedAt.Value + MsdbTime.ParseDuration(row.RunDuration),
                Status = status,
                ErrorText = errorText,
            });
        }

        return (runs, lastOutcomeInstanceId);
    }

    // ---------------------------------------------------------------------------------------
    // In-flight runs from sysjobactivity
    // ---------------------------------------------------------------------------------------

    private sealed class ActivityRow
    {
        public Guid JobId { get; set; }
        public DateTime StartExecutionDate { get; set; }
    }

    /// <summary>Jobs the current Agent session has started and not yet stopped. Closed later by their outcome row.</summary>
    private async Task<List<CollectedJobRun>> CollectInFlightRunsAsync(SqlConnection msdb, CancellationToken cancellationToken)
    {
        var rows = await msdb.QueryAsync<ActivityRow>(new CommandDefinition(@"
            SELECT
                ja.job_id               AS JobId,
                ja.start_execution_date AS StartExecutionDate
            FROM msdb.dbo.sysjobactivity AS ja
            WHERE ja.session_id = (SELECT MAX(session_id) FROM msdb.dbo.syssessions)
              AND ja.start_execution_date IS NOT NULL
              AND ja.stop_execution_date IS NULL;",
            cancellationToken: cancellationToken));

        var runs = new List<CollectedJobRun>();
        foreach (var row in rows)
        {
            var startedAt = MsdbTime.ToUtc(row.StartExecutionDate, _sourceZone)!.Value;
            runs.Add(new CollectedJobRun
            {
                NativeJobId = row.JobId.ToString("D"),
                NativeRunId = MsdbTime.ExecutionKey(row.JobId, startedAt),
                StartedAt = startedAt,
                EndedAt = null,
                Status = Collectors.RunStatus.Running,
                ErrorText = null,
            });
        }

        return runs;
    }

    // ---------------------------------------------------------------------------------------
    // Purge-gap detection
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// If the oldest surviving history row is above the watermark, rows we never read were purged.
    /// Identity gaps (a restart can skip identity values) only inflate the reported count; they
    /// cannot fire this on their own, because older rows would still be present.
    /// </summary>
    private async Task CheckHistoryPurgeGapAsync(SqlConnection msdb, long since, CancellationToken cancellationToken)
    {
        if (since == 0)
        {
            return;
        }

        var minInstanceId = await msdb.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT MIN(instance_id) FROM msdb.dbo.sysjobhistory;",
            cancellationToken: cancellationToken));

        if (minInstanceId is { } min && min > since + 1)
        {
            await RaiseAlertAsync(
                AlertType.SqlAgentHistoryPurgeGap,
                $"{_instanceName}: sysjobhistory purged past the watermark ({since} -> oldest surviving {min}); up to {min - since - 1} rows missed",
                cancellationToken);
        }
    }

    /// <summary><c>sysjobhistory.run_status</c> and <c>sysjobservers.last_run_outcome</c> share this encoding.</summary>
    internal static string MapRunStatus(int runStatus) => runStatus switch
    {
        0 => Collectors.RunStatus.Failed,
        1 => Collectors.RunStatus.Succeeded,
        2 => Collectors.RunStatus.Retry,
        3 => Collectors.RunStatus.Cancelled,
        4 => Collectors.RunStatus.Running,
        _ => Collectors.RunStatus.Unknown,
    };
}
