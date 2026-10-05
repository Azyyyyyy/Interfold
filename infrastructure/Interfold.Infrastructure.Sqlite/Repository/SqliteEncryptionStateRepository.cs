using Dapper;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;

namespace Interfold.Infrastructure.Sqlite.Repository;

public sealed class SqliteEncryptionStateRepository : IEncryptionStateRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public SqliteEncryptionStateRepository(ISqliteConnectionFactory connectionFactory, TimeProvider timeProvider)
    {
        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider;
    }

    public async Task<EncryptionState?> GetAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QueryFirstOrDefaultAsync<EncryptionStateRow>(
            """
            SELECT encryption_initialized AS Initialized, encryption_key_checksum AS Checksum, salt AS Salt
            FROM encryption_states
            WHERE system_id = @system_id
            LIMIT 1
            """,
            new { system_id = systemId.Value });
        if (row is null)
        {
            return null;
        }

        return new EncryptionState(
            row.Initialized != 0,
            KeyChecksum.FromNullable(row.Checksum),
            EncryptionSalt.FromNullable(row.Salt));
    }

    public async Task<bool> UpsertAsync(
        SystemId systemId,
        bool initialized,
        KeyChecksum? keyChecksum,
        EncryptionSalt? salt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.ExecuteAsync(
            """
            INSERT INTO encryption_states
                (system_id, encryption_initialized, encryption_key_checksum, salt, updated_at)
            VALUES
                (@system_id, @initialized, @checksum, @salt, @updated_at)
            ON CONFLICT(system_id) DO UPDATE SET
                encryption_initialized = excluded.encryption_initialized,
                encryption_key_checksum = excluded.encryption_key_checksum,
                salt = COALESCE(excluded.salt, encryption_states.salt),
                updated_at = excluded.updated_at
            """,
            new
            {
                system_id = systemId.Value,
                initialized = initialized ? 1 : 0,
                checksum = keyChecksum?.Value,
                salt = salt?.Value,
                updated_at = nowMs,
            });
        return rows > 0;
    }

    private sealed record EncryptionStateRow(long Initialized, string? Checksum, string? Salt);
}
