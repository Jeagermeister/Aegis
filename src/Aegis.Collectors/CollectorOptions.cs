namespace Aegis.Collectors;

/// <summary>Bound from the <c>Collectors</c> configuration section by <see cref="ServiceCollectionExtensions.AddAegisCollectors"/>.</summary>
public sealed class CollectorOptions
{
    public const string SectionName = "Collectors";

    /// <summary>The AEGIS store. Filled from <c>ConnectionStrings:Aegis</c> when not set here.</summary>
    public string StoreConnectionString { get; set; } = string.Empty;

    /// <summary>Seconds between polls. Each collector is independently timed, so a slow source never delays the others.</summary>
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// A source whose heartbeat is older than this is stale. Defaults to twice the poll interval
    /// when not set, so a single missed poll never fires the alert.
    /// </summary>
    public int StaleAfterSeconds { get; set; } = 0;

    public List<SqlAgentCollectorOptions> SqlAgentCollectors { get; set; } = [];

    public List<AirflowCollectorOptions> AirflowCollectors { get; set; } = [];
}

public sealed class SqlAgentCollectorOptions
{
    /// <summary>Becomes <c>SourceSystem.Name</c>. Unique per type.</summary>
    public string InstanceName { get; set; } = string.Empty;

    /// <summary>
    /// The monitored instance. Needs read access to msdb only. The credentials are never stored;
    /// only the server and database names are recorded in <c>SourceSystem.Config</c>.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// IANA or Windows id of the monitored instance's time zone, because msdb stores local
    /// wall-clock time. Defaults to UTC, which is also what the dev container runs in.
    /// </summary>
    public string TimeZoneId { get; set; } = "UTC";
}

public sealed class AirflowCollectorOptions
{
    /// <summary>Becomes <c>SourceSystem.Name</c>. Unique per type.</summary>
    public string InstanceName { get; set; } = string.Empty;

    /// <summary>The webserver root, e.g. <c>http://localhost:8080</c>. <c>/api/v1</c> is appended.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>How far back to read DAG runs the first time this source is polled, before it has a watermark.</summary>
    public int InitialLookbackDays { get; set; } = 1;
}
