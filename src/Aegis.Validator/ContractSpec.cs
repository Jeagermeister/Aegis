namespace Aegis.Validator;

/// <summary>
/// A per-feed data contract, as a team edits it in YAML and commits to git. The stable key is
/// <see cref="FeedId"/> (stored as <c>Feed.Name</c>); <see cref="Owner"/> and
/// <see cref="LandingPrefix"/> are promoted to <c>Feed</c> columns, and everything else is the
/// versioned spec (<c>ContractVersion.SpecJson</c>). See DESIGN-v2, contract layer.
/// </summary>
public sealed class ContractSpec
{
    /// <summary>Stable feed identifier, e.g. <c>BCBS-ELIG</c>. Becomes <c>Feed.Name</c>.</summary>
    public string FeedId { get; set; } = string.Empty;

    /// <summary>The team that owns the feed. Becomes <c>Feed.TeamId</c>; alerts route here.</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>Landing-zone prefix (S3 prefix or path pattern). Becomes <c>Feed.LandingPrefix</c>.</summary>
    public string LandingPrefix { get; set; } = string.Empty;

    /// <summary>Expected file name pattern, e.g. <c>BCBS_ELIG_{yyyyMMdd}.csv</c>.</summary>
    public string FileMask { get; set; } = string.Empty;

    /// <summary>The arrival window (SLA): when the file is expected, and when it is late.</summary>
    public ArrivalWindow ArrivalWindow { get; set; } = new();

    /// <summary>The expected shape of the file: columns, types, nullability.</summary>
    public ContractSchema Schema { get; set; } = new();

    /// <summary>Throws when a required field is missing or inconsistent. Called before hashing or persisting.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(FeedId))
        {
            throw new ContractSpecException("feedId is required");
        }

        if (string.IsNullOrWhiteSpace(Owner))
        {
            throw new ContractSpecException($"feed '{FeedId}': owner is required");
        }

        if (string.IsNullOrWhiteSpace(LandingPrefix))
        {
            throw new ContractSpecException($"feed '{FeedId}': landingPrefix is required");
        }

        if (string.IsNullOrWhiteSpace(FileMask))
        {
            throw new ContractSpecException($"feed '{FeedId}': fileMask is required");
        }

        if (ArrivalWindow.Start >= ArrivalWindow.End)
        {
            throw new ContractSpecException($"feed '{FeedId}': arrival window start must be before end");
        }

        if (string.IsNullOrWhiteSpace(Schema.Name))
        {
            throw new ContractSpecException($"feed '{FeedId}': schema name is required");
        }

        if (Schema.Columns.Count == 0)
        {
            throw new ContractSpecException($"feed '{FeedId}': schema must declare at least one column");
        }

        foreach (var column in Schema.Columns)
        {
            if (string.IsNullOrWhiteSpace(column.Name))
            {
                throw new ContractSpecException($"feed '{FeedId}': a column is missing a name");
            }

            if (string.IsNullOrWhiteSpace(column.Type))
            {
                throw new ContractSpecException($"feed '{FeedId}': column '{column.Name}' is missing a type");
            }
        }
    }
}

/// <summary>The arrival SLA: the file is expected from <see cref="Start"/> and is late after <see cref="End"/>.</summary>
public sealed class ArrivalWindow
{
    public TimeOnly Start { get; set; }

    public TimeOnly End { get; set; }
}

/// <summary>The expected shape of a feed's file.</summary>
public sealed class ContractSchema
{
    public string Name { get; set; } = string.Empty;

    public List<ColumnSpec> Columns { get; set; } = [];
}

/// <summary>One expected column: name, type, and whether it may be null.</summary>
public sealed class ColumnSpec
{
    public string Name { get; set; } = string.Empty;

    /// <summary>One of <c>string</c>, <c>date</c>, <c>decimal</c>, <c>int</c>.</summary>
    public string Type { get; set; } = string.Empty;

    public bool Nullable { get; set; }

    public int? MaxLength { get; set; }
}

/// <summary>A contract that cannot be parsed or fails validation.</summary>
public sealed class ContractSpecException(string message) : Exception(message);
