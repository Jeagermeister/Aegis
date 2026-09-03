using Aegis.Validator;

const string Usage = """
    AEGIS contract sync: reads contract YAML files and syncs them to the store.

    Usage: aegis-validator [--dir=DIR] [--connection=CONNECTION_STRING]

      --dir         Directory of contract .yaml/.yml files. Default: ./contracts
      --connection  AEGIS store connection string. Default: the AEGIS_CONNECTION_STRING
                    environment variable, or the local dev stack.
    """;

if (args.Any(argument => argument is "--help" or "-h"))
{
    Console.WriteLine(Usage);
    return 0;
}

var directory = Option("--dir") ?? "contracts";
var connectionString = Option("--connection")
    ?? Environment.GetEnvironmentVariable("AEGIS_CONNECTION_STRING")
    ?? "Server=localhost,1433;Database=Aegis;User Id=sa;Password=Aegis!Dev2026;TrustServerCertificate=True";

if (!Directory.Exists(directory))
{
    Console.Error.WriteLine($"Contract directory not found: {directory}");
    return 2;
}

var store = new ContractStore(connectionString);
var results = await store.SyncAsync(directory);

foreach (var result in results)
{
    if (result.Error is not null)
    {
        Console.Error.WriteLine($"{result.FeedId}: ERROR {result.Error}");
    }
    else if (result.VersionCreated)
    {
        Console.WriteLine($"{result.FeedId}: created version {result.Version}");
    }
    else
    {
        Console.WriteLine($"{result.FeedId}: unchanged (version {result.Version})");
    }
}

var failures = results.Count(result => result.Error is not null);
Console.WriteLine($"{results.Count} contract(s) synced, {failures} failed.");
return failures == 0 ? 0 : 1;

string? Option(string name)
    => args.FirstOrDefault(argument => argument.StartsWith(name + "=", StringComparison.Ordinal)) is { } match
        ? match[(name.Length + 1)..]
        : null;
