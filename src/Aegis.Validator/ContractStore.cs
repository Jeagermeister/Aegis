using Dapper;
using Microsoft.Data.SqlClient;

namespace Aegis.Validator;

/// <summary>
/// The git→DB sync for contracts (roadmap 2.1). Reads every <c>.yaml</c>/<c>.yml</c> file in a
/// directory, parses and validates each, upserts the <c>Feed</c> row, and appends a new
/// <c>ContractVersion</c> only when the spec hash changed since the latest version. A contract
/// that fails validation is reported and skipped, never stored.
/// </summary>
public sealed class ContractStore
{
    private readonly string _connectionString;

    public ContractStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <summary>One contract file's sync outcome.</summary>
    public sealed record SyncResult(string FeedId, bool VersionCreated, int Version, string? Error);

    /// <summary>
    /// Syncs every contract file under <paramref name="directory"/>. Returns one result per file,
    /// including failures (with <see cref="SyncResult.Error"/> set) so a bad file does not abort
    /// the rest of the sync.
    /// </summary>
    public async Task<IReadOnlyList<SyncResult>> SyncAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var files = Directory.EnumerateFiles(directory, "*.yaml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(directory, "*.yml", SearchOption.AllDirectories))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        var results = new List<SyncResult>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var yaml = await File.ReadAllTextAsync(file, cancellationToken);
                var spec = ContractParser.Parse(yaml);
                results.Add(await SyncOneAsync(spec, cancellationToken));
            }
            catch (ContractSpecException ex)
            {
                results.Add(new SyncResult(Path.GetFileNameWithoutExtension(file), false, 0, ex.Message));
            }
        }

        return results;
    }

    /// <summary>Upserts the feed and appends a version when the spec changed. Returns the current version number.</summary>
    public async Task<SyncResult> SyncOneAsync(ContractSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        spec.Validate();

        var specHash = ContractParser.ComputeSpecHash(spec);
        var specJson = ContractParser.ToCanonicalJson(spec);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var feedId = await UpsertFeedAsync(connection, transaction, spec, cancellationToken);

        var latest = await connection.QuerySingleOrDefaultAsync<(int Version, string SpecHash)>(new CommandDefinition(@"
            SELECT TOP 1 Version, SpecHash
            FROM dbo.ContractVersion
            WHERE FeedId = @FeedId
            ORDER BY Version DESC;",
            new { FeedId = feedId },
            cancellationToken: cancellationToken,
            transaction: transaction));

        if (latest.SpecHash == specHash)
        {
            await transaction.CommitAsync(cancellationToken);
            return new SyncResult(spec.FeedId, false, latest.Version, null);
        }

        var nextVersion = latest.Version + 1;
        await connection.ExecuteAsync(new CommandDefinition(@"
            INSERT INTO dbo.ContractVersion (FeedId, Version, SpecHash, SpecJson, EffectiveFrom, CreatedAt)
            VALUES (@FeedId, @Version, @SpecHash, @SpecJson, SYSUTCDATETIME(), SYSUTCDATETIME());",
            new { FeedId = feedId, Version = nextVersion, SpecHash = specHash, SpecJson = specJson },
            cancellationToken: cancellationToken,
            transaction: transaction));

        await transaction.CommitAsync(cancellationToken);
        return new SyncResult(spec.FeedId, true, nextVersion, null);
    }

    private static async Task<int> UpsertFeedAsync(SqlConnection connection, SqlTransaction transaction, ContractSpec spec, CancellationToken cancellationToken)
    {
        var existing = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT Id FROM dbo.Feed WHERE Name = @Name;",
            new { Name = spec.FeedId },
            cancellationToken: cancellationToken,
            transaction: transaction));

        if (existing is { } id)
        {
            await connection.ExecuteAsync(new CommandDefinition(@"
                UPDATE dbo.Feed
                SET TeamId = @TeamId,
                    LandingPrefix = @LandingPrefix,
                    UpdatedAt = SYSUTCDATETIME()
                WHERE Id = @Id;",
                new { Id = id, TeamId = spec.Owner, LandingPrefix = spec.LandingPrefix },
                cancellationToken: cancellationToken,
                transaction: transaction));
            return id;
        }

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
            INSERT INTO dbo.Feed (Name, TeamId, LandingPrefix, IsActive, CreatedAt, UpdatedAt)
            VALUES (@Name, @TeamId, @LandingPrefix, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            SELECT CAST(SCOPE_IDENTITY() AS INT);",
            new { Name = spec.FeedId, TeamId = spec.Owner, LandingPrefix = spec.LandingPrefix },
            cancellationToken: cancellationToken,
            transaction: transaction));
    }
}
