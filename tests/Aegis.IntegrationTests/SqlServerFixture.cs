using Aegis.Migrations;
using DbUp;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Aegis.IntegrationTests;

/// <summary>
/// One real SQL Server per test class, migrated to the current schema with the same DbUp
/// pipeline production uses. No in-memory doubles at the store seam (TECH-STACK, testing).
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        ConnectionString = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = "AegisIntegration",
        }.ConnectionString;

        EnsureDatabase.For.SqlDatabase(ConnectionString);

        var result = DeployChanges.To
            .SqlDatabase(ConnectionString)
            .WithScriptsEmbeddedInAssembly(typeof(MigrationScripts).Assembly)
            .LogToNowhere()
            .Build()
            .PerformUpgrade();

        if (!result.Successful)
        {
            throw new InvalidOperationException($"Migrations failed: {result.Error}", result.Error);
        }
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
