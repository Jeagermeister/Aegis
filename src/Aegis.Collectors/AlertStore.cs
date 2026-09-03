using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Aegis.Collectors;

/// <summary>
/// Writes to the <c>Alert</c> table. Raising re-fires an existing alert (bumps
/// <c>Occurrences</c>, sets <c>Status</c> back to <c>Firing</c>); resolving marks a firing alert
/// <c>Resolved</c>. Shared by the collectors (zero-row, sharp-drop) and the stale-collector sweep,
/// so the dedup/status semantics live in one place. The message is logged only: the table has no
/// text column yet.
/// </summary>
public sealed class AlertStore
{
    private readonly string _storeConnectionString;
    private readonly ILogger _logger;

    public AlertStore(string storeConnectionString, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeConnectionString);
        ArgumentNullException.ThrowIfNull(logger);

        _storeConnectionString = storeConnectionString;
        _logger = logger;
    }

    public async Task RaiseAsync(string type, string dedupKey, string message, CancellationToken cancellationToken)
    {
        _logger.LogWarning("Alert {AlertType}: {Message}", type, message);

        await using var connection = new SqlConnection(_storeConnectionString);
        await connection.ExecuteAsync(new CommandDefinition(@"
            MERGE dbo.Alert WITH (HOLDLOCK) AS target
            USING (SELECT @DedupKey AS DedupKey) AS source
                ON target.DedupKey = source.DedupKey
            WHEN MATCHED THEN
                UPDATE SET Status = 'Firing',
                           LastOccurrence = SYSUTCDATETIME(),
                           Occurrences = target.Occurrences + 1,
                           UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (Type, DedupKey, Status, RoutedTo, FirstSeen, LastOccurrence, Occurrences, CreatedAt, UpdatedAt)
                VALUES (@Type, @DedupKey, 'Firing', NULL, SYSUTCDATETIME(), SYSUTCDATETIME(), 1, SYSUTCDATETIME(), SYSUTCDATETIME());",
            new { Type = type, DedupKey = dedupKey },
            cancellationToken: cancellationToken));
    }

    /// <summary>Marks a firing alert resolved. A no-op when it is not firing, so a healthy source never resurrects history.</summary>
    public async Task ResolveAsync(string dedupKey, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_storeConnectionString);
        await connection.ExecuteAsync(new CommandDefinition(@"
            UPDATE dbo.Alert
            SET Status = 'Resolved',
                UpdatedAt = SYSUTCDATETIME()
            WHERE DedupKey = @DedupKey AND Status = 'Firing';",
            new { DedupKey = dedupKey },
            cancellationToken: cancellationToken));
    }
}
