using Dapper;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Infrastructure.Sqlite;

public sealed class SqliteIdempotencyStore : IIdempotencyStore
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public SqliteIdempotencyStore(ISqliteConnectionFactory connectionFactory, TimeProvider timeProvider)
    {
        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider;
    }

    public async Task<IdempotencyMatch?> FindAsync(
        SystemId principalId,
        OperationId operationId,
        IdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QueryFirstOrDefaultAsync<IdempotencyRow>(
            """
            SELECT payload_hash AS PayloadHash, outcome_hash AS OutcomeHash, outcome_payload AS OutcomePayload
            FROM octocon_idempotency
            WHERE principal_id = @principal_id
              AND operation_id = @operation_id
              AND idempotency_key = @idempotency_key
            """,
            new
            {
                principal_id = principalId,
                operation_id = operationId.Value,
                idempotency_key = idempotencyKey.Value,
            });
        return row is null ? null : new IdempotencyMatch(row.PayloadHash, row.OutcomeHash, row.OutcomePayload);
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
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO octocon_idempotency (
                principal_id, operation_id, idempotency_key,
                payload_hash, outcome_hash, outcome_payload, created_at
            ) VALUES (
                @principal_id, @operation_id, @idempotency_key,
                @payload_hash, @outcome_hash, @outcome_payload, @created_at
            )
            ON CONFLICT(principal_id, operation_id, idempotency_key) DO UPDATE SET
                payload_hash = excluded.payload_hash,
                outcome_hash = excluded.outcome_hash,
                outcome_payload = excluded.outcome_payload
            """,
            new
            {
                principal_id = principalId,
                operation_id = operationId.Value,
                idempotency_key = idempotencyKey.Value,
                payload_hash = payloadHash,
                outcome_hash = outcomeHash,
                outcome_payload = outcomePayload,
                created_at = nowMs,
            });
    }

    private sealed record IdempotencyRow(string PayloadHash, string OutcomeHash, string? OutcomePayload);
}
