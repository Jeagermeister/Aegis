namespace Aegis.Collectors;

/// <summary>Values written to <c>JobRun.Status</c>. Collectors map their scheduler's native states onto these.</summary>
public static class RunStatus
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";

    /// <summary>The scheduler will run it again (SQL Agent step retry). Not terminal.</summary>
    public const string Retry = "Retry";

    /// <summary>A state this collector does not recognise. Kept open rather than guessed closed.</summary>
    public const string Unknown = "Unknown";

    /// <summary>True when the run has finished, whatever the outcome. Only a terminal state may close an open run.</summary>
    public static bool IsTerminal(string status) => status is Succeeded or Failed or Cancelled;
}

/// <summary>Values written to <c>CatalogSync.Status</c>.</summary>
public static class SyncStatus
{
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

/// <summary>Values written to <c>Alert.Type</c> by the collector layer ("monitor the monitor").</summary>
public static class AlertType
{
    /// <summary>The source answered, but with no jobs. Wrong permissions and filtered views look exactly like this.</summary>
    public const string CollectorZeroRows = "CollectorZeroRows";

    /// <summary>The job count fell well below its trailing baseline.</summary>
    public const string CollectorSharpDrop = "CollectorSharpDrop";

    /// <summary><c>sysjobhistory</c> was purged past the watermark; runs were lost at the source.</summary>
    public const string SqlAgentHistoryPurgeGap = "SqlAgentHistoryPurgeGap";

    /// <summary>The source stopped heartbeating; the collector is dead or its store write path broke.</summary>
    public const string CollectorStale = "CollectorStale";
}

/// <summary>Values written to <c>JobOwnership.ParsedFrom</c>: which field the owner was harvested from.</summary>
public static class OwnerSource
{
    /// <summary>A first-class owner field on the scheduler (Airflow <c>owners</c>).</summary>
    public const string Declared = "Owner";

    /// <summary>An <c>Owner:</c>/<c>Team:</c> declaration or a <c>#tag</c>/<c>@mention</c> in the description.</summary>
    public const string Description = "Description";

    /// <summary>A <c>Ticket:</c> reference in the description. A ticket is not a team; see the review notes.</summary>
    public const string Ticket = "Ticket";

    /// <summary>A scheduler-side tag in owner form (<c>team:etl</c>, <c>#etl</c>).</summary>
    public const string Tags = "Tags";

    /// <summary>Nothing parseable. These jobs are the delegable gap list.</summary>
    public const string None = "None";
}
