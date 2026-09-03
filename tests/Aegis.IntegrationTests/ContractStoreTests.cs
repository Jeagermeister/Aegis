using Aegis.Validator;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Aegis.IntegrationTests;

/// <summary>
/// The git→DB contract sync (roadmap 2.1): a contract file becomes a <c>Feed</c> row plus a
/// versioned <c>ContractVersion</c>, and a re-sync with an unchanged spec does not create a new
/// version.
/// </summary>
public sealed class ContractStoreTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private const string ContractYaml = """
        feedId: BCBS-ELIG
        owner: ETL
        landingPrefix: landing/bcbs/eligibility/
        fileMask: BCBS_ELIG_{yyyyMMdd}.csv
        arrivalWindow:
          start: "02:00"
          end: "06:00"
        schema:
          name: Eligibility_v1
          columns:
            - name: MemberId
              type: string
              nullable: false
              maxLength: 50
            - name: TermDate
              type: date
              nullable: true
        """;

    [Fact]
    public async Task A_new_contract_creates_a_feed_and_version_1()
    {
        var store = new ContractStore(fixture.ConnectionString);
        var result = await store.SyncOneAsync(ContractParser.Parse(ContractYaml));

        Assert.True(result.VersionCreated);
        Assert.Equal(1, result.Version);

        await using var db = new SqlConnection(fixture.ConnectionString);
        var feed = await db.QuerySingleAsync<(string Name, string TeamId, string LandingPrefix)>(
            "SELECT Name, TeamId, LandingPrefix FROM dbo.Feed WHERE Name = 'BCBS-ELIG'");
        Assert.Equal(("BCBS-ELIG", "ETL", "landing/bcbs/eligibility/"), feed);

        var version = await db.QuerySingleAsync<(int Version, string SpecHash)>(
            "SELECT Version, SpecHash FROM dbo.ContractVersion WHERE FeedId = (SELECT Id FROM dbo.Feed WHERE Name = 'BCBS-ELIG')");
        Assert.Equal(1, version.Version);
        Assert.Equal(64, version.SpecHash.Length);
    }

    [Fact]
    public async Task An_unchanged_contract_does_not_create_a_new_version()
    {
        var store = new ContractStore(fixture.ConnectionString);
        var spec = ContractParser.Parse(ContractYaml);

        var first = await store.SyncOneAsync(spec);
        var second = await store.SyncOneAsync(spec);

        Assert.True(first.VersionCreated);
        Assert.False(second.VersionCreated);
        Assert.Equal(first.Version, second.Version);

        await using var db = new SqlConnection(fixture.ConnectionString);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.ContractVersion WHERE FeedId = (SELECT Id FROM dbo.Feed WHERE Name = 'BCBS-ELIG')"));
    }

    [Fact]
    public async Task A_changed_contract_appends_the_next_version()
    {
        var store = new ContractStore(fixture.ConnectionString);
        var spec = ContractParser.Parse(ContractYaml);

        await store.SyncOneAsync(spec);
        var changed = ContractParser.Parse(ContractYaml.Replace("owner: ETL", "owner: Claims", StringComparison.Ordinal));
        var result = await store.SyncOneAsync(changed);

        Assert.True(result.VersionCreated);
        Assert.Equal(2, result.Version);

        await using var db = new SqlConnection(fixture.ConnectionString);
        Assert.Equal(2, await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.ContractVersion WHERE FeedId = (SELECT Id FROM dbo.Feed WHERE Name = 'BCBS-ELIG')"));
        Assert.Equal("Claims", await db.ExecuteScalarAsync<string>(
            "SELECT TeamId FROM dbo.Feed WHERE Name = 'BCBS-ELIG'"));
    }

    [Fact]
    public async Task A_directory_sync_skips_invalid_files_and_reports_them()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aegis-contract-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "good.yaml"), ContractYaml);
            await File.WriteAllTextAsync(Path.Combine(dir, "bad.yaml"), "feedId: BROKEN\n");

            var store = new ContractStore(fixture.ConnectionString);
            var results = await store.SyncAsync(dir);

            Assert.Equal(2, results.Count);
            Assert.Contains(results, r => r.FeedId == "good" && r.VersionCreated);
            Assert.Contains(results, r => r.FeedId == "bad" && r.Error is not null);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
