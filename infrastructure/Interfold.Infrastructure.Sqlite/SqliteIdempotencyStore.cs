using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Infrastructure.Sqlite;

public sealed class SqliteIdempotencyStore(ISqliteConnectionFactory connectionFactory) : IIdempotencyStore
{
    public async Task<IdempotencyMatch?> FindAsync(
        SystemId principalId,
        OperationId operationId,
        IdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_hash, outcome_hash, outcome_payload
            FROM octocon_idempotency
            WHERE principal_id = $principal_id
              AND operation_id = $operation_id
              AND idempotency_key = $idempotency_key
            """;
        command.Parameters.AddWithValue("$principal_id", principalId.Value);
        command.Parameters.AddWithValue("$operation_id", operationId.Value);
        command.Parameters.AddWithValue("$idempotency_key", idempotencyKey.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var payloadHash = reader.GetString(0);
        var outcomeHash = reader.GetString(1);
        var outcomePayload = await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2);
        return new IdempotencyMatch(payloadHash, outcomeHash, outcomePayload);
    }

    public async Task SaveAsync(
        SystemId principalId,
        OperationId operationId,
        IdempotencyKey idempotencyKey,
        string payloadHash,
        string outcomeHash,
        string? outcomePayload,
        CancellationToken cancellationToken = default)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO octocon_idempotency (
                principal_id, operation_id, idempotency_key,
                payload_hash, outcome_hash, outcome_payload, created_at
            ) VALUES (
                $principal_id, $operation_id, $idempotency_key,
                $payload_hash, $outcome_hash, $outcome_payload, $created_at
            )
            ON CONFLICT(principal_id, operation_id, idempotency_key) DO UPDATE SET
                payload_hash = excluded.payload_hash,
                outcome_hash = excluded.outcome_hash,
                outcome_payload = excluded.outcome_payload
            """;
        command.Parameters.AddWithValue("$principal_id", principalId.Value);
        command.Parameters.AddWithValue("$operation_id", operationId.Value);
        command.Parameters.AddWithValue("$idempotency_key", idempotencyKey.Value);
        command.Parameters.AddWithValue("$payload_hash", payloadHash);
        command.Parameters.AddWithValue("$outcome_hash", outcomeHash);
        command.Parameters.AddWithValue("$outcome_payload", (object?)outcomePayload ?? DBNull.Value);
        command.Parameters.AddWithValue("$created_at", nowMs);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
