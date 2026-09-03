using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aegis.Collectors;

/// <summary>
/// Reads Airflow (including MWAA) through the stable REST API v1. Polls incrementally with
/// <c>updated_at_gte</c> and pages every listing, because MWAA rate-limits and the server clamps
/// <c>limit</c> to <c>[api] maximum_page_limit</c> (100 by default) without saying so.
///
/// <para>Timestamps are Airflow's own (<c>start_date</c>, <c>end_date</c>), never the collector's
/// arrival time, so scheduler lag stays visible instead of being folded into MTTD.</para>
///
/// <para>The watermark is the UTC instant captured <em>before</em> the poll's first request.
/// Anything updated while the requests were in flight is read again next time and deduplicated,
/// so nothing slips through the gap between "list fetched" and "watermark taken".</para>
/// </summary>
public sealed class AirflowCollector : CollectorBase
{
    public const string Type = "Airflow";

    /// <summary>Airflow's default <c>maximum_page_limit</c>. Asking for more is silently clamped, so ask for exactly this.</summary>
    private const int PageSize = 100;

    /// <summary>Airflow's default owner when a DAG declares none. Treated as unowned so the gap list stays honest.</summary>
    private const string DefaultOwner = "airflow";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _http;
    private readonly string _instanceName;
    private readonly TimeSpan _initialLookback;

    /// <param name="http">Already pointed at the instance and authenticated; see <see cref="ConfigureClient"/>.</param>
    /// <param name="initialLookback">How far back to read runs when there is no watermark yet.</param>
    public AirflowCollector(
        string storeConnectionString,
        int sourceSystemId,
        HttpClient http,
        string instanceName,
        TimeSpan initialLookback,
        ILogger logger)
        : base(storeConnectionString, sourceSystemId, logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);

        _http = http;
        _instanceName = instanceName;
        _initialLookback = initialLookback;
    }

    public override string SourceSystemType => Type;

    public override string SourceSystemName => _instanceName;

    /// <summary>
    /// Points a client at an Airflow instance with Basic auth. Airflow must list
    /// <c>airflow.api.auth.backend.basic_auth</c> in <c>[api] auth_backends</c>; the default
    /// (session only) answers 401 to every API call. MWAA uses a short-lived token instead, which
    /// is a different configuration of the same client, not a different collector.
    /// </summary>
    public static void ConfigureClient(HttpClient client, string baseUrl, string username, string password)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        // Trailing slash so relative paths resolve under any path prefix the instance is served from.
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    protected override async Task<CollectedBatch> CollectDataAsync(string? watermark, CancellationToken cancellationToken)
    {
        var since = DateTimeOffset.TryParse(
                watermark, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow - _initialLookback;

        var pollStartedAt = DateTimeOffset.UtcNow;

        // only_active=false keeps DAGs whose files were removed, so they land as inactive rather than vanishing.
        var dags = await GetAllPagesAsync<DagPage, Dag>("api/v1/dags?only_active=false", page => page.Dags, cancellationToken);
        var jobs = dags.Select(ToJob).ToList();

        var sinceParameter = Uri.EscapeDataString(since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        var dagRuns = await GetAllPagesAsync<DagRunPage, DagRun>(
            $"api/v1/dags/~/dagRuns?updated_at_gte={sinceParameter}&order_by=id",
            page => page.DagRuns,
            cancellationToken);
        var runs = dagRuns.Select(ToRun).ToList();

        Logger.LogDebug("{InstanceName}: {DagCount} DAGs, {RunCount} runs updated since {Since:O}", _instanceName, jobs.Count, runs.Count, since);

        return new CollectedBatch(jobs, runs, pollStartedAt.ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Walks a paged listing to the end using <c>offset</c> and <c>total_entries</c>. Any non-2xx
    /// answer throws: a 401 or a 404 must fail the sync, not quietly produce an empty catalog.
    /// </summary>
    private async Task<List<TItem>> GetAllPagesAsync<TPage, TItem>(string pathAndQuery, Func<TPage, List<TItem>?> items, CancellationToken cancellationToken)
        where TPage : PageBase
    {
        var all = new List<TItem>();
        var offset = 0;

        while (true)
        {
            var separator = pathAndQuery.Contains('?') ? '&' : '?';
            var url = string.Create(CultureInfo.InvariantCulture, $"{pathAndQuery}{separator}limit={PageSize}&offset={offset}");

            using var response = await _http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var detail = body.Length > 300 ? body[..300] + "..." : body;
                throw new HttpRequestException($"Airflow answered {(int)response.StatusCode} {response.ReasonPhrase} for {url}: {detail}");
            }

            var page = await response.Content.ReadFromJsonAsync<TPage>(JsonOptions, cancellationToken)
                ?? throw new InvalidOperationException($"Airflow returned an empty body for {url}");

            var batch = items(page) ?? [];
            all.AddRange(batch);
            offset += batch.Count;

            if (batch.Count == 0 || offset >= page.TotalEntries)
            {
                return all;
            }
        }
    }

    private static CollectedJob ToJob(Dag dag)
    {
        var owners = (dag.Owners ?? [])
            .Where(owner => !string.IsNullOrWhiteSpace(owner) && !owner.Equals(DefaultOwner, StringComparison.OrdinalIgnoreCase))
            .Select(owner => owner.Trim())
            .ToList();

        return new CollectedJob
        {
            NativeId = dag.DagId,
            NativeName = string.IsNullOrWhiteSpace(dag.DagDisplayName) ? dag.DagId : dag.DagDisplayName,
            Description = dag.Description ?? string.Empty,
            IsActive = dag.IsActive && !dag.IsPaused,
            DeclaredOwner = owners.Count > 0 ? string.Join(", ", owners) : null,
            Tags = (dag.Tags ?? []).Select(tag => tag.Name).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).ToList(),
            NextRunAt = dag.NextDagrun,
        };
    }

    private static CollectedJobRun ToRun(DagRun run)
    {
        var status = MapState(run.State);

        return new CollectedJobRun
        {
            NativeJobId = run.DagId,
            NativeRunId = run.DagRunId,
            // A queued run has no start yet; the logical date is still Airflow's timestamp.
            StartedAt = run.StartDate ?? run.LogicalDate ?? run.ExecutionDate ?? DateTimeOffset.UtcNow,
            EndedAt = run.EndDate,
            Status = status,
            ErrorText = status == RunStatus.Failed ? DescribeFailure(run) : null,
        };
    }

    /// <summary>
    /// A DAG run carries no error message; the failing task's log does. Until task instances are
    /// read (a later enrichment), the fingerprint groups failures per DAG, which is the useful unit.
    /// </summary>
    private static string DescribeFailure(DagRun run)
        => string.IsNullOrWhiteSpace(run.Note)
            ? $"Airflow DAG {run.DagId} run failed ({run.RunType ?? "unknown trigger"})"
            : $"Airflow DAG {run.DagId} run failed ({run.RunType ?? "unknown trigger"}): {run.Note}";

    /// <summary>The four DAG-run states Airflow 2.x defines. Anything else stays open as Unknown rather than being guessed.</summary>
    internal static string MapState(string? state) => state switch
    {
        "success" => RunStatus.Succeeded,
        "failed" => RunStatus.Failed,
        "running" => RunStatus.Running,
        "queued" => RunStatus.Queued,
        _ => RunStatus.Unknown,
    };

    // Wire shapes. Property names are snake_cased by JsonOptions: NextDagrun -> next_dagrun, DagRunId -> dag_run_id.

    private abstract class PageBase
    {
        public int TotalEntries { get; set; }
    }

    private sealed class DagPage : PageBase
    {
        public List<Dag>? Dags { get; set; }
    }

    private sealed class DagRunPage : PageBase
    {
        public List<DagRun>? DagRuns { get; set; }
    }

    private sealed class Dag
    {
        public string DagId { get; set; } = string.Empty;
        public string? DagDisplayName { get; set; }
        public string? Description { get; set; }
        public bool IsPaused { get; set; }
        public bool IsActive { get; set; } = true;
        public List<string>? Owners { get; set; }
        public List<DagTag>? Tags { get; set; }
        public DateTimeOffset? NextDagrun { get; set; }
    }

    private sealed class DagTag
    {
        public string? Name { get; set; }
    }

    private sealed class DagRun
    {
        public string DagId { get; set; } = string.Empty;
        public string DagRunId { get; set; } = string.Empty;
        public string? State { get; set; }
        public string? RunType { get; set; }
        public DateTimeOffset? LogicalDate { get; set; }
        public DateTimeOffset? ExecutionDate { get; set; }
        public DateTimeOffset? StartDate { get; set; }
        public DateTimeOffset? EndDate { get; set; }
        public string? Note { get; set; }
    }
}
