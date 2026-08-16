using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Microsoft.Data.Sqlite;

namespace Interfold.Infrastructure.Sqlite.Repository;

public sealed class SqliteEncryptionStateRepository(ISqliteConnectionFactory connectionFactory)
    : IEncryptionStateRepository
{
    public async Task<EncryptionState?> GetAsync(SystemId systemId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = SqliteStorageKeys.Normalize(systemId).Value;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT encryption_initialized, encryption_key_checksum, salt
            FROM encryption_states
            WHERE system_id = $system_id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$system_id", normalized);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new EncryptionState(
            reader.GetInt64(0) != 0,
            KeyChecksum.FromNullable(reader.IsDBNull(1) ? null : reader.GetString(1)),
            EncryptionSalt.FromNullable(reader.IsDBNull(2) ? null : reader.GetString(2)));
    }

    public async Task<bool> UpsertAsync(
        SystemId systemId,
        bool initialized,
        KeyChecksum? keyChecksum,
        EncryptionSalt? salt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = SqliteStorageKeys.Normalize(systemId).Value;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // COALESCE keeps an existing salt when the caller omits one (InMemory Upsert contract).
        command.CommandText = """
            INSERT INTO encryption_states
                (system_id, encryption_initialized, encryption_key_checksum, salt, updated_at)
            VALUES
                ($system_id, $initialized, $checksum, $salt, $updated_at)
            ON CONFLICT(system_id) DO UPDATE SET
                encryption_initialized = excluded.encryption_initialized,
                encryption_key_checksum = excluded.encryption_key_checksum,
                salt = COALESCE(excluded.salt, encryption_states.salt),
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$system_id", normalized);
        command.Parameters.AddWithValue("$initialized", initialized ? 1 : 0);
        command.Parameters.AddWithValue("$checksum", (object?)keyChecksum?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$salt", (object?)salt?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated_at", nowMs);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }
}
