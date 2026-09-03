using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CsvHelper;
using CsvHelper.Configuration;

namespace Aegis.Generator;

/// <summary>A carrier feed as its landing-zone contract will describe it: which file, when, and what shape.</summary>
public sealed record FeedDefinition(
    string FeedId,
    string CarrierName,
    string FeedName,
    string FileMask,
    string SchemaName,
    TimeOnly ArrivalWindowStart,
    TimeOnly ArrivalWindowEnd,
    int ExpectedFileCountPerDay,
    bool IsActive)
{
    /// <summary>Fixed-width feeds are the <c>.txt</c> ones; everything else is delimited.</summary>
    public bool IsFixedWidth => FileMask.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
}

public sealed record ColumnDefinition(
    string Name,
    string DataType,
    bool IsNullable,
    int? MaxLength = null);

public sealed record SchemaDefinition(
    string SchemaId,
    string Name,
    IReadOnlyList<ColumnDefinition> Columns);

/// <param name="ViolationRate">Probability, per file, of carrying one injected violation. 0 is a clean day; 1 breaks every feed.</param>
public sealed record GenerationContext(
    DateOnly GenerationDate,
    IReadOnlyList<FeedDefinition> Feeds,
    IReadOnlyList<SchemaDefinition> Schemas,
    string OutputDirectory,
    double ViolationRate);

public sealed record ViolationSpec(
    ViolationType Type,
    string Description,
    double Weight);

public enum ViolationType
{
    LateArrival,
    MissingFile,
    MaskDrift,
    SchemaDriftRenamedColumn,
    SchemaDriftAddedColumn,
    SchemaDriftRemovedColumn,
    NullInNotNullColumn,
    DataTypeMismatch,
    TruncatedData,
    DuplicateRows,
}

/// <summary>
/// What was written and what was done to it. The validator's tests read this as their oracle:
/// every injected violation names the file it is in and, where it matters, the column.
/// </summary>
public sealed record GenerationManifest(
    DateTimeOffset GeneratedAt,
    DateOnly GenerationDate,
    double ViolationRate,
    int? Seed,
    IReadOnlyList<FeedDefinition> Feeds,
    IReadOnlyList<SchemaDefinition> Schemas,
    IReadOnlyList<GeneratedFile> Files);

/// <param name="ExpectedFileName">What the contract's mask expects for the day.</param>
/// <param name="RelativePath">Where it was actually written, relative to the output directory. Null when it was deliberately not written.</param>
/// <param name="ScheduledArrival">A simulated arrival inside the feed's window.</param>
/// <param name="ActualArrival">When the simulated landing zone should see it. After the window for a late file; null for a missing one.</param>
/// <param name="Violation">Null for a clean file.</param>
public sealed record GeneratedFile(
    string FeedId,
    string ExpectedFileName,
    string? RelativePath,
    string Format,
    int RowCount,
    DateTimeOffset ScheduledArrival,
    DateTimeOffset? ActualArrival,
    InjectedViolation? Violation);

public sealed record InjectedViolation(ViolationType Type, string Detail);

/// <summary>
/// Fake insurers dropping files with known defects. Triples as the validator's test suite and the
/// <c>docker compose up</c> public demo (DESIGN-v2, local test stack).
/// Metadata-only posture still holds: every value here is invented; nothing resembles a real
/// member, claim, or provider.
/// </summary>
public sealed class SyntheticFeedGenerator
{
    /// <summary>
    /// A late file is written under its real name plus this suffix. Whatever simulates the landing
    /// zone renames it into place once <see cref="GeneratedFile.ActualArrival"/> has passed.
    /// </summary>
    public const string LateFileSuffix = ".late";

    private const string CsvFormat = "csv";
    private const string FixedWidthFormat = "fixed-width";
    private const int DefaultColumnWidth = 20;

    private static readonly FeedDefinition[] DefaultFeeds =
    [
        new("BCBS-ELIG", "BCBS", "Eligibility", "BCBS_ELIG_{yyyyMMdd}.csv", "Eligibility_v1", new TimeOnly(2, 0), new TimeOnly(6, 0), 1, true),
        new("BCBS-CLAIMS", "BCBS", "Claims", "BCBS_CLAIMS_{yyyyMMdd}.csv", "Claims_v1", new TimeOnly(3, 0), new TimeOnly(7, 0), 1, true),
        new("AETNA-ELIG", "Aetna", "Eligibility", "AETNA_ELIG_{yyyyMMdd}.txt", "Eligibility_v1", new TimeOnly(1, 0), new TimeOnly(5, 0), 1, true),
        new("AETNA-CLAIMS", "Aetna", "Claims", "AETNA_CLAIMS_{yyyyMMdd}.txt", "Claims_v1", new TimeOnly(2, 0), new TimeOnly(6, 0), 1, true),
        new("HUMANA-ELIG", "Humana", "Eligibility", "HUMANA_ELIG_{yyyyMMdd}.csv", "Eligibility_v1", new TimeOnly(0, 0), new TimeOnly(4, 0), 1, true),
        new("CIGNA-ELIG", "Cigna", "Eligibility", "CIGNA_ELIG_{yyyyMMdd}.csv", "Eligibility_v1", new TimeOnly(3, 0), new TimeOnly(7, 0), 1, true),
        new("ANTHEM-CLAIMS", "Anthem", "Claims", "ANTHEM_CLAIMS_{yyyyMMdd}.csv", "Claims_v1", new TimeOnly(4, 0), new TimeOnly(8, 0), 1, true),
        new("UHC-ELIG", "UnitedHealthcare", "Eligibility", "UHC_ELIG_{yyyyMMdd}.txt", "Eligibility_v1", new TimeOnly(1, 0), new TimeOnly(5, 0), 1, true),
    ];

    private static readonly SchemaDefinition[] DefaultSchemas =
    [
        new("Eligibility_v1", "Eligibility",
        [
            new ColumnDefinition("MemberId", "string", false, 50),
            new ColumnDefinition("FirstName", "string", false, 100),
            new ColumnDefinition("LastName", "string", false, 100),
            new ColumnDefinition("DOB", "date", false),
            new ColumnDefinition("Gender", "string", false, 1),
            new ColumnDefinition("PlanCode", "string", false, 20),
            new ColumnDefinition("EffectiveDate", "date", false),
            new ColumnDefinition("TermDate", "date", true),
            new ColumnDefinition("CarrierId", "string", false, 10),
            new ColumnDefinition("GroupNumber", "string", true, 30),
            new ColumnDefinition("SubscriberId", "string", false, 50),
            new ColumnDefinition("Relationship", "string", true, 20),
        ]),
        new("Claims_v1", "Claims",
        [
            new ColumnDefinition("ClaimId", "string", false, 50),
            new ColumnDefinition("MemberId", "string", false, 50),
            new ColumnDefinition("ServiceDate", "date", false),
            new ColumnDefinition("ProviderNPI", "string", false, 10),
            new ColumnDefinition("ProcedureCode", "string", false, 10),
            new ColumnDefinition("DiagnosisCode", "string", false, 10),
            new ColumnDefinition("BilledAmount", "decimal", false),
            new ColumnDefinition("AllowedAmount", "decimal", true),
            new ColumnDefinition("PaidAmount", "decimal", true),
            new ColumnDefinition("CarrierId", "string", false, 10),
            new ColumnDefinition("ClaimStatus", "string", false, 20),
            new ColumnDefinition("ProcessedDate", "date", true),
        ]),
    ];

    private static readonly ViolationSpec[] ViolationCatalog =
    [
        new(ViolationType.LateArrival, "File arrives after the window closes", 0.15),
        new(ViolationType.MissingFile, "File never arrives", 0.10),
        new(ViolationType.MaskDrift, "File name no longer matches the mask (date format or prefix changed)", 0.10),
        new(ViolationType.SchemaDriftRenamedColumn, "Column renamed (e.g. EffectiveDate -> Effective_Date)", 0.12),
        new(ViolationType.SchemaDriftAddedColumn, "Unexpected column added", 0.08),
        new(ViolationType.SchemaDriftRemovedColumn, "Expected column missing", 0.08),
        new(ViolationType.NullInNotNullColumn, "NULL in a declared NOT NULL column", 0.15),
        new(ViolationType.DataTypeMismatch, "Text in a numeric or date column", 0.07),
        new(ViolationType.TruncatedData, "Value longer than the column's declared length (delimited feeds only)", 0.05),
        new(ViolationType.DuplicateRows, "Duplicate primary-key rows", 0.05),
    ];

    private static readonly string[] FirstNames = ["Avery", "Jordan", "Riley", "Morgan", "Casey", "Quinn", "Rowan", "Skyler", "Dakota", "Emerson", "Finley", "Harper"];
    private static readonly string[] LastNames = ["Okafor", "Lindqvist", "Nakamura", "Delgado", "Brennan", "Haddad", "Kowalski", "Mbeki", "Rasmussen", "Iyer", "Castellano", "Whitfield"];
    private static readonly string[] Genders = ["M", "F", "U"];
    private static readonly string[] Relationships = ["Self", "Spouse", "Child", "Dependent"];
    private static readonly string[] ClaimStatuses = ["Paid", "Denied", "Pending", "Adjusted"];

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Random _random;
    private readonly int? _seed;

    /// <param name="seed">Makes the whole output reproducible, file names and values included.</param>
    public SyntheticFeedGenerator(int? seed = null)
    {
        _seed = seed;
        _random = seed is { } value ? new Random(value) : new Random();
    }

    public static GenerationContext CreateDefaultContext(
        DateOnly? date = null,
        string? outputDirectory = null,
        double violationRate = 0.25)
    {
        var generationDate = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var directory = outputDirectory
            ?? Path.Combine(Directory.GetCurrentDirectory(), "generated_feeds", generationDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture));

        return new GenerationContext(
            GenerationDate: generationDate,
            Feeds: DefaultFeeds.Where(feed => feed.IsActive).ToList(),
            Schemas: DefaultSchemas,
            OutputDirectory: directory,
            ViolationRate: violationRate);
    }

    /// <summary>Writes one day of feeds under <c>{OutputDirectory}/{Carrier}/</c> plus <c>MANIFEST.json</c>, and returns the manifest.</summary>
    public async Task<GenerationManifest> GenerateAsync(GenerationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ViolationRate is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(context), "ViolationRate must be between 0 and 1");
        }

        Directory.CreateDirectory(context.OutputDirectory);

        var files = new List<GeneratedFile>();
        foreach (var carrier in context.Feeds.Where(feed => feed.IsActive).GroupBy(feed => feed.CarrierName))
        {
            var carrierDirectory = carrier.Key.Replace(' ', '_');
            Directory.CreateDirectory(Path.Combine(context.OutputDirectory, carrierDirectory));

            foreach (var feed in carrier)
            {
                var schema = context.Schemas.FirstOrDefault(candidate => candidate.SchemaId == feed.SchemaName)
                    ?? throw new InvalidOperationException($"Schema {feed.SchemaName} not found for feed {feed.FeedId}");

                files.AddRange(await GenerateFeedFilesAsync(feed, schema, carrierDirectory, context, cancellationToken));
            }
        }

        var manifest = new GenerationManifest(
            GeneratedAt: DateTimeOffset.UtcNow,
            GenerationDate: context.GenerationDate,
            ViolationRate: context.ViolationRate,
            Seed: _seed,
            Feeds: context.Feeds,
            Schemas: context.Schemas,
            Files: files);

        var manifestPath = Path.Combine(context.OutputDirectory, "MANIFEST.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, ManifestJsonOptions), cancellationToken);

        return manifest;
    }

    // ---------------------------------------------------------------------------------------
    // Per feed
    // ---------------------------------------------------------------------------------------

    private async Task<List<GeneratedFile>> GenerateFeedFilesAsync(
        FeedDefinition feed,
        SchemaDefinition schema,
        string carrierDirectory,
        GenerationContext context,
        CancellationToken cancellationToken)
    {
        var files = new List<GeneratedFile>();
        var format = feed.IsFixedWidth ? FixedWidthFormat : CsvFormat;

        for (var fileIndex = 0; fileIndex < feed.ExpectedFileCountPerDay; fileIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var expectedFileName = ExpectedFileName(feed, context.GenerationDate, fileIndex);
            var scheduledArrival = ScheduledArrival(feed, context.GenerationDate);
            var violation = PickViolation(feed, context.ViolationRate);

            if (violation?.Type == ViolationType.MissingFile)
            {
                files.Add(new GeneratedFile(feed.FeedId, expectedFileName, null, format, 0, scheduledArrival, null,
                    new InjectedViolation(ViolationType.MissingFile, "File was never written")));
                continue;
            }

            var actualFileName = expectedFileName;
            var actualArrival = scheduledArrival;
            InjectedViolation? injected = null;

            if (violation?.Type == ViolationType.MaskDrift)
            {
                actualFileName = DriftFileName(feed, context.GenerationDate, fileIndex, out var detail);
                injected = new InjectedViolation(ViolationType.MaskDrift, detail);
            }

            var (columns, rows, rowDetail) = GenerateRows(schema, context.GenerationDate, violation);
            if (rowDetail is not null)
            {
                injected = new InjectedViolation(violation!.Type, rowDetail);
            }

            if (violation?.Type == ViolationType.LateArrival)
            {
                var lateBy = TimeSpan.FromMinutes(_random.Next(30, 240));
                actualArrival = WindowClose(feed, context.GenerationDate) + lateBy;
                actualFileName += LateFileSuffix;
                injected = new InjectedViolation(ViolationType.LateArrival, $"Arrives {lateBy.TotalMinutes:F0} min after the window closes");
            }

            var content = feed.IsFixedWidth
                ? BuildFixedWidthContent(columns, rows)
                : BuildCsvContent(columns, rows);

            var relativePath = Path.Combine(carrierDirectory, actualFileName);
            await File.WriteAllTextAsync(Path.Combine(context.OutputDirectory, relativePath), content, cancellationToken);

            files.Add(new GeneratedFile(feed.FeedId, expectedFileName, relativePath, format, rows.Count, scheduledArrival, actualArrival, injected));
        }

        return files;
    }

    private static string ExpectedFileName(FeedDefinition feed, DateOnly date, int fileIndex)
    {
        var name = feed.FileMask.Replace("{yyyyMMdd}", date.ToString("yyyyMMdd", CultureInfo.InvariantCulture), StringComparison.Ordinal);
        if (feed.ExpectedFileCountPerDay > 1)
        {
            var extension = Path.GetExtension(name);
            name = string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileNameWithoutExtension(name)}_{fileIndex + 1}{extension}");
        }

        return name;
    }

    /// <summary>The two drifts carriers actually ship: a different date format, or a different prefix case.</summary>
    private string DriftFileName(FeedDefinition feed, DateOnly date, int fileIndex, out string detail)
    {
        var expected = ExpectedFileName(feed, date, fileIndex);
        if (_random.Next(2) == 0)
        {
            var drifted = feed.FileMask.Replace("{yyyyMMdd}", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal);
            detail = $"Date format drifted: expected {expected}, got {drifted}";
            return drifted;
        }

        var lowered = Path.GetFileNameWithoutExtension(expected).ToLowerInvariant() + Path.GetExtension(expected);
        detail = $"Prefix case drifted: expected {expected}, got {lowered}";
        return lowered;
    }

    private DateTimeOffset ScheduledArrival(FeedDefinition feed, DateOnly date)
    {
        var window = feed.ArrivalWindowEnd - feed.ArrivalWindowStart;
        var offset = TimeSpan.FromTicks((long)(window.Ticks * _random.NextDouble()));
        return new DateTimeOffset(date.ToDateTime(feed.ArrivalWindowStart, DateTimeKind.Utc)) + offset;
    }

    private static DateTimeOffset WindowClose(FeedDefinition feed, DateOnly date)
        => new(date.ToDateTime(feed.ArrivalWindowEnd, DateTimeKind.Utc));

    /// <summary>
    /// With probability <paramref name="violationRate"/> the file gets exactly one violation, chosen by
    /// catalog weight. Truncation cannot be represented in fixed-width output (the width <em>is</em>
    /// the truncation), so it is never picked for those feeds.
    /// </summary>
    private ViolationSpec? PickViolation(FeedDefinition feed, double violationRate)
    {
        if (_random.NextDouble() >= violationRate)
        {
            return null;
        }

        var candidates = feed.IsFixedWidth
            ? ViolationCatalog.Where(spec => spec.Type != ViolationType.TruncatedData).ToArray()
            : ViolationCatalog;

        var roll = _random.NextDouble() * candidates.Sum(spec => spec.Weight);
        var cumulative = 0.0;
        foreach (var spec in candidates)
        {
            cumulative += spec.Weight;
            if (roll < cumulative)
            {
                return spec;
            }
        }

        return candidates[^1];
    }

    // ---------------------------------------------------------------------------------------
    // Rows and the mutations that make them wrong
    // ---------------------------------------------------------------------------------------

    private sealed record OutputColumn(string Name, int Width);

    /// <summary>
    /// Generates clean rows, then applies the one violation that touches row content. Returns the
    /// <em>effective</em> column list: after a rename, add, or removal, the header written to the
    /// file must reflect the drift, or the drift is not in the file at all.
    /// </summary>
    private (List<OutputColumn> Columns, List<Dictionary<string, object?>> Rows, string? Detail) GenerateRows(
        SchemaDefinition schema,
        DateOnly date,
        ViolationSpec? violation)
    {
        var columns = schema.Columns.Select(column => new OutputColumn(column.Name, WidthOf(column))).ToList();
        var rows = new List<Dictionary<string, object?>>();
        var rowCount = _random.Next(50, 500);

        for (var i = 0; i < rowCount; i++)
        {
            var row = new Dictionary<string, object?>();
            foreach (var column in schema.Columns)
            {
                row[column.Name] = column.IsNullable && _random.NextDouble() < 0.15
                    ? null
                    : GenerateValue(column, date);
            }

            rows.Add(row);
        }

        string? detail = violation?.Type switch
        {
            ViolationType.NullInNotNullColumn => InjectNulls(schema, rows),
            ViolationType.SchemaDriftRenamedColumn => RenameColumn(schema, columns, rows),
            ViolationType.SchemaDriftAddedColumn => AddColumn(columns, rows),
            ViolationType.SchemaDriftRemovedColumn => RemoveColumn(schema, columns, rows),
            ViolationType.DataTypeMismatch => InjectTypeMismatch(schema, rows),
            ViolationType.TruncatedData => InjectOverlongValues(schema, rows),
            ViolationType.DuplicateRows => DuplicateRows(rows),
            _ => null,
        };

        return (columns, rows, detail);
    }

    private string? InjectNulls(SchemaDefinition schema, List<Dictionary<string, object?>> rows)
    {
        var candidates = schema.Columns.Where(column => !column.IsNullable).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var target = candidates[_random.Next(candidates.Count)];
        var affected = PickDistinctRows(rows.Count, _random.Next(1, Math.Min(10, rows.Count)));
        foreach (var index in affected)
        {
            rows[index][target.Name] = null;
        }

        return $"{target.Name}: {affected.Count} NULL(s) in a NOT NULL column";
    }

    private string? RenameColumn(SchemaDefinition schema, List<OutputColumn> columns, List<Dictionary<string, object?>> rows)
    {
        var candidates = schema.Columns.Where(column => column.Name is not ("MemberId" or "ClaimId")).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var target = candidates[_random.Next(candidates.Count)];
        var newName = DriftedColumnName(target.Name);

        var position = columns.FindIndex(column => column.Name == target.Name);
        columns[position] = columns[position] with { Name = newName };
        foreach (var row in rows)
        {
            row[newName] = row[target.Name];
            row.Remove(target.Name);
        }

        return $"{target.Name} renamed to {newName}";
    }

    /// <summary>EffectiveDate becomes Effective_Date, Provider_NPI, and so on; an all-caps name gets a suffix.</summary>
    private static string DriftedColumnName(string name)
    {
        if (name.Contains('_', StringComparison.Ordinal))
        {
            return name.Replace("_", string.Empty, StringComparison.Ordinal);
        }

        for (var i = name.Length - 1; i > 0; i--)
        {
            if (char.IsUpper(name[i]) && char.IsLower(name[i - 1]))
            {
                return name[..i] + "_" + name[i..];
            }
        }

        return name + "_New";
    }

    private string AddColumn(List<OutputColumn> columns, List<Dictionary<string, object?>> rows)
    {
        var name = string.Create(CultureInfo.InvariantCulture, $"Unexpected_Column_{_random.Next(1000, 9999)}");
        columns.Add(new OutputColumn(name, DefaultColumnWidth));
        foreach (var row in rows)
        {
            row[name] = "UNEXPECTED";
        }

        return $"{name} added";
    }

    private string? RemoveColumn(SchemaDefinition schema, List<OutputColumn> columns, List<Dictionary<string, object?>> rows)
    {
        var candidates = schema.Columns.Where(column => column.IsNullable).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var target = candidates[_random.Next(candidates.Count)];
        columns.RemoveAll(column => column.Name == target.Name);
        foreach (var row in rows)
        {
            row.Remove(target.Name);
        }

        return $"{target.Name} removed";
    }

    private string? InjectTypeMismatch(SchemaDefinition schema, List<Dictionary<string, object?>> rows)
    {
        var candidates = schema.Columns.Where(column => column.DataType is "date" or "decimal" or "int").ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var target = candidates[_random.Next(candidates.Count)];
        var affected = PickDistinctRows(rows.Count, _random.Next(1, Math.Min(5, rows.Count)));
        foreach (var index in affected)
        {
            rows[index][target.Name] = "NOT_A_" + target.DataType.ToUpperInvariant();
        }

        return $"{target.Name}: {affected.Count} text value(s) in a {target.DataType} column";
    }

    private string? InjectOverlongValues(SchemaDefinition schema, List<Dictionary<string, object?>> rows)
    {
        var candidates = schema.Columns.Where(column => column.MaxLength is > 1).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var target = candidates[_random.Next(candidates.Count)];
        var affected = PickDistinctRows(rows.Count, _random.Next(1, Math.Min(5, rows.Count)));
        var overBy = _random.Next(10, 50);
        foreach (var index in affected)
        {
            rows[index][target.Name] = new string('X', target.MaxLength!.Value + overBy);
        }

        return $"{target.Name}: {affected.Count} value(s) exceed max length {target.MaxLength} by {overBy}";
    }

    private string DuplicateRows(List<Dictionary<string, object?>> rows)
    {
        var count = _random.Next(2, 6);
        for (var i = 0; i < count; i++)
        {
            rows.Add(new Dictionary<string, object?>(rows[_random.Next(rows.Count)]));
        }

        return $"{count} duplicate row(s) appended";
    }

    private HashSet<int> PickDistinctRows(int rowCount, int wanted)
    {
        var picked = new HashSet<int>();
        while (picked.Count < Math.Min(wanted, rowCount))
        {
            picked.Add(_random.Next(rowCount));
        }

        return picked;
    }

    // ---------------------------------------------------------------------------------------
    // Values
    // ---------------------------------------------------------------------------------------

    private object GenerateValue(ColumnDefinition column, DateOnly date)
    {
        return column.Name switch
        {
            "FirstName" => Pick(FirstNames),
            "LastName" => Pick(LastNames),
            "Gender" => Pick(Genders),
            "Relationship" => Pick(Relationships),
            "ClaimStatus" => Pick(ClaimStatuses),
            "DOB" => IsoDate(date.AddYears(-_random.Next(18, 90)).AddDays(-_random.Next(0, 365))),
            "EffectiveDate" => IsoDate(date.AddDays(-_random.Next(0, 730))),
            "TermDate" => IsoDate(date.AddDays(_random.Next(30, 400))),
            "ServiceDate" => IsoDate(date.AddDays(-_random.Next(0, 30))),
            "ProcessedDate" => IsoDate(date.AddDays(-_random.Next(0, 10))),
            "ProviderNPI" => Digits(10),
            "ProcedureCode" => Digits(5),
            "DiagnosisCode" => string.Create(CultureInfo.InvariantCulture, $"{(char)('A' + _random.Next(26))}{Digits(2)}.{Digits(1)}"),
            _ => column.DataType switch
            {
                "string" => GenerateString(column.Name, column.MaxLength ?? 50),
                "date" => IsoDate(date.AddDays(-_random.Next(0, 30))),
                "decimal" => Math.Round(_random.NextDouble() * 10000 + 1, 2),
                "int" => _random.Next(1, 10000),
                _ => GenerateString(column.Name, column.MaxLength ?? 50),
            },
        };
    }

    private string Pick(string[] values) => values[_random.Next(values.Length)];

    private static string IsoDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private string Digits(int count)
    {
        var buffer = new char[count];
        for (var i = 0; i < count; i++)
        {
            buffer[i] = (char)('0' + _random.Next(10));
        }

        return new string(buffer);
    }

    /// <summary>Prefixed identifiers, drawn from the seeded generator so a seed reproduces the whole file.</summary>
    private string GenerateString(string columnName, int maxLength)
    {
        var prefix = columnName switch
        {
            "MemberId" => "M",
            "SubscriberId" => "S",
            "ClaimId" => "C",
            "PlanCode" => "PLN",
            "GroupNumber" => "GRP",
            "CarrierId" => "CAR",
            _ => "VAL",
        };

        var available = maxLength - prefix.Length - 1;
        if (available < 1)
        {
            return Digits(maxLength);
        }

        return prefix + "_" + Digits(Math.Min(12, available));
    }

    private static int WidthOf(ColumnDefinition column) => column.DataType switch
    {
        "date" => 10,
        "decimal" => 12,
        "int" => 10,
        _ => column.MaxLength ?? DefaultColumnWidth,
    };

    // ---------------------------------------------------------------------------------------
    // Writers
    // ---------------------------------------------------------------------------------------

    private static string BuildCsvContent(List<OutputColumn> columns, List<Dictionary<string, object?>> rows)
    {
        using var writer = new StringWriter();
        using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = true });

        foreach (var column in columns)
        {
            csv.WriteField(column.Name);
        }

        csv.NextRecord();

        foreach (var row in rows)
        {
            foreach (var column in columns)
            {
                csv.WriteField(row.GetValueOrDefault(column.Name));
            }

            csv.NextRecord();
        }

        return writer.ToString();
    }

    /// <summary>Header line included, every field padded or cut to its width. Cutting is why truncation cannot be injected here.</summary>
    private static string BuildFixedWidthContent(List<OutputColumn> columns, List<Dictionary<string, object?>> rows)
    {
        var builder = new StringBuilder();

        builder.AppendLine(string.Concat(columns.Select(column => Fit(column.Name, column.Width))));

        foreach (var row in rows)
        {
            builder.AppendLine(string.Concat(columns.Select(column =>
                Fit(Convert.ToString(row.GetValueOrDefault(column.Name), CultureInfo.InvariantCulture) ?? string.Empty, column.Width))));
        }

        return builder.ToString();
    }

    private static string Fit(string value, int width)
        => value.Length > width ? value[..width] : value.PadRight(width);
}
