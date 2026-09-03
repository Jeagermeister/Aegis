using DbUp;
using DbUp.Engine;

var connectionString = args.Length > 0
    ? args[0]
    : Environment.GetEnvironmentVariable("AEGIS_CONNECTION_STRING")
        ?? "Server=localhost,1433;Database=Aegis;User Id=sa;Password=Aegis!Dev2026;TrustServerCertificate=True";

EnsureDatabase.For.SqlDatabase(connectionString);

var result = DeployChanges.To
    .SqlDatabase(connectionString)
    .WithScriptsEmbeddedInAssembly(typeof(Program).Assembly)
    .LogToConsole()
    .Build()
    .PerformUpgrade();

if (!result.Successful)
{
    Console.Error.WriteLine($"Migration failed: {result.Error}");
    return 1;
}

Console.WriteLine("Migrations applied successfully.");
return 0;
