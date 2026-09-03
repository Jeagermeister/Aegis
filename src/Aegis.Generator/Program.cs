using System.Globalization;
using Aegis.Generator;
using Microsoft.Extensions.Logging;

const string Usage = """
    Synthetic carrier-feed generator.

    Usage: aegis-generator [--date=yyyy-MM-dd] [--output=DIR] [--violation-rate=0..1] [--seed=N]

      --date            Day the feeds are for. Default: today (UTC).
      --output          Output directory. Default: ./generated_feeds/{yyyyMMdd}
      --violation-rate  Probability that each file carries one injected violation. Default: 0.25
      --seed            Makes the run reproducible.
    """;

if (args.Any(argument => argument is "--help" or "-h"))
{
    Console.WriteLine(Usage);
    return 0;
}

using var loggerFactory = LoggerFactory.Create(logging => logging.AddSimpleConsole(console =>
{
    console.SingleLine = true;
    console.TimestampFormat = "HH:mm:ss ";
}));
var logger = loggerFactory.CreateLogger("generator");

DateOnly date;
double violationRate;
int? seed;
try
{
    date = Option("--date") is { } dateText
        ? DateOnly.ParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture)
        : DateOnly.FromDateTime(DateTime.UtcNow);
    violationRate = Option("--violation-rate") is { } rateText
        ? double.Parse(rateText, CultureInfo.InvariantCulture)
        : 0.25;
    seed = Option("--seed") is { } seedText
        ? int.Parse(seedText, CultureInfo.InvariantCulture)
        : null;
}
catch (FormatException ex)
{
    logger.LogError("Bad argument: {Message}", ex.Message);
    Console.WriteLine(Usage);
    return 2;
}

var context = SyntheticFeedGenerator.CreateDefaultContext(date, Option("--output"), violationRate);
var generator = new SyntheticFeedGenerator(seed);

logger.LogInformation("Generating feeds for {Date} into {Output} (violation rate {Rate:P0}{Seed})",
    date, context.OutputDirectory, violationRate, seed is { } s ? $", seed {s}" : string.Empty);

try
{
    var manifest = await generator.GenerateAsync(context);

    foreach (var file in manifest.Files)
    {
        if (file.Violation is { } violation)
        {
            logger.LogWarning("{FeedId}: {Path} [{Type}] {Detail}", file.FeedId, file.RelativePath ?? file.ExpectedFileName, violation.Type, violation.Detail);
        }
        else
        {
            logger.LogInformation("{FeedId}: {Path} ({Rows} rows)", file.FeedId, file.RelativePath, file.RowCount);
        }
    }

    logger.LogInformation("{Files} file(s), {Violations} with violations; manifest at {Manifest}",
        manifest.Files.Count, manifest.Files.Count(file => file.Violation is not null), Path.Combine(context.OutputDirectory, "MANIFEST.json"));
    return 0;
}
catch (Exception ex)
{
    logger.LogError(ex, "Generation failed");
    return 1;
}

string? Option(string name)
    => args.FirstOrDefault(argument => argument.StartsWith(name + "=", StringComparison.Ordinal)) is { } match
        ? match[(name.Length + 1)..]
        : null;
