using Interfold.Settings.Contracts.Ids;
using Interfold.Settings.Contracts.Models.ImportOperations;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Domain.Abstractions;
using Microsoft.Data.Sqlite;

namespace Interfold.Infrastructure.Sqlite.Repository;

/// <summary>SQLite port of <see cref="IImportOperationRepository"/>. The per-system mutex
/// is <c>INSERT … ON CONFLICT DO NOTHING</c> on <c>active_import_by_system</c>; terminal
/// transitions release with a conditional <c>DELETE … AND operation_id = ?</c>.</summary>
public sealed class SqliteImportOperationRepository(
    ISqliteConnectionFactory connectionFactory,
    IRegionContext regionContext,
    TimeProvider? timeProvider = null) : IImportOperationRepository
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ImportOperationClaim> TryClaimAsync(
        SystemId systemId,
        ImportOperationKind kind,
        IdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var scopedSystemId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        var kindWire = kind.ToWire();
        var newOperationId = Guid.NewGuid();
        var nowMs = NowMs();

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var claim = connection.CreateCommand())
            {
                claim.Transaction = tx;
                claim.CommandText = """
                    INSERT INTO active_import_by_system (system_id, kind, operation_id, started_at)
                    VALUES ($system_id, $kind, $operation_id, $started_at)
                    ON CONFLICT(system_id, kind) DO NOTHING
                    """;
                claim.Parameters.AddWithValue("$system_id", scopedSystemId);
                claim.Parameters.AddWithValue("$kind", kindWire);
                claim.Parameters.AddWithValue("$operation_id", FormatOperationId(newOperationId));
                claim.Parameters.AddWithValue("$started_at", nowMs);
                var applied = await claim.ExecuteNonQueryAsync(cancellationToken);
                if (applied == 0)
                {
                    await using var existingCmd = connection.CreateCommand();
                    existingCmd.Transaction = tx;
                    existingCmd.CommandText = """
                        SELECT operation_id
                        FROM active_import_by_system
                        WHERE system_id = $system_id AND kind = $kind
                        LIMIT 1
                        """;
                    existingCmd.Parameters.AddWithValue("$system_id", scopedSystemId);
                    existingCmd.Parameters.AddWithValue("$kind", kindWire);
                    var existingRaw = (string?)await existingCmd.ExecuteScalarAsync(cancellationToken);
                    await tx.CommitAsync(cancellationToken);

                    if (existingRaw is null)
                    {
                        throw new InvalidOperationException(
                            "[import-ops] Claim conflicted but no active row was found — unexpected race.");
                    }

                    return new ImportOperationClaim(new ImportOperationId(ParseOperationId(existingRaw)), IsNew: false);
                }
            }

            await using (var history = connection.CreateCommand())
            {
                history.Transaction = tx;
                history.CommandText = """
                    INSERT INTO import_operations
                        (system_id, operation_id, kind, status, started_at, idempotency_key)
                    VALUES
                        ($system_id, $operation_id, $kind, $status, $started_at, $idempotency_key)
                    """;
                history.Parameters.AddWithValue("$system_id", scopedSystemId);
                history.Parameters.AddWithValue("$operation_id", FormatOperationId(newOperationId));
                history.Parameters.AddWithValue("$kind", kindWire);
                history.Parameters.AddWithValue("$status", ImportOperationStatus.Queued.ToWire());
                history.Parameters.AddWithValue("$started_at", nowMs);
                history.Parameters.AddWithValue("$idempotency_key", idempotencyKey.Value);
                await history.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            return new ImportOperationClaim(new ImportOperationId(newOperationId), IsNew: true);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task MarkRunningAsync(
        SystemId systemId,
        ImportOperationId operationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var scopedSystemId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE import_operations
            SET status = $running
            WHERE system_id = $system_id
              AND operation_id = $operation_id
              AND status = $queued
            """;
        cmd.Parameters.AddWithValue("$running", ImportOperationStatus.Running.ToWire());
        cmd.Parameters.AddWithValue("$system_id", scopedSystemId);
        cmd.Parameters.AddWithValue("$operation_id", FormatOperationId(operationId.Value));
        cmd.Parameters.AddWithValue("$queued", ImportOperationStatus.Queued.ToWire());
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkSucceededAsync(
        SystemId systemId,
        ImportOperationId operationId,
        ImportOperationKind kind,
        int alterCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var scopedSystemId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        var nowMs = NowMs();
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = """
                    UPDATE import_operations
                    SET status = $status, finished_at = $finished_at, alter_count = $alter_count
                    WHERE system_id = $system_id AND operation_id = $operation_id
                    """;
                update.Parameters.AddWithValue("$status", ImportOperationStatus.Succeeded.ToWire());
                update.Parameters.AddWithValue("$finished_at", nowMs);
                update.Parameters.AddWithValue("$alter_count", alterCount);
                update.Parameters.AddWithValue("$system_id", scopedSystemId);
                update.Parameters.AddWithValue("$operation_id", FormatOperationId(operationId.Value));
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await ReleaseSlotAsync(connection, tx, scopedSystemId, kind.ToWire(), operationId.Value, cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task MarkFailedAsync(
        SystemId systemId,
        ImportOperationId operationId,
        ImportOperationKind kind,
        ImportErrorCode errorCode,
        string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var scopedSystemId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        var nowMs = NowMs();
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = """
                    UPDATE import_operations
                    SET status = $status,
                        finished_at = $finished_at,
                        error_code = $error_code,
                        error_message = $error_message
                    WHERE system_id = $system_id AND operation_id = $operation_id
                    """;
                update.Parameters.AddWithValue("$status", ImportOperationStatus.Failed.ToWire());
                update.Parameters.AddWithValue("$finished_at", nowMs);
                update.Parameters.AddWithValue("$error_code", errorCode.ToWire());
                update.Parameters.AddWithValue("$error_message", (object?)errorMessage ?? DBNull.Value);
                update.Parameters.AddWithValue("$system_id", scopedSystemId);
                update.Parameters.AddWithValue("$operation_id", FormatOperationId(operationId.Value));
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await ReleaseSlotAsync(connection, tx, scopedSystemId, kind.ToWire(), operationId.Value, cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<ImportOperationSnapshot?> GetByIdAsync(
        SystemId systemId,
        ImportOperationId operationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var scopedSystemId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT system_id, operation_id, kind, status, started_at, finished_at,
                   alter_count, error_code, error_message, idempotency_key
            FROM import_operations
            WHERE system_id = $system_id AND operation_id = $operation_id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$system_id", scopedSystemId);
        cmd.Parameters.AddWithValue("$operation_id", FormatOperationId(operationId.Value));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return MapRow(reader);
    }

    public async Task<ImportOperationId?> GetActiveOperationIdAsync(
        SystemId systemId,
        ImportOperationKind kind,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var scopedSystemId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT operation_id
            FROM active_import_by_system
            WHERE system_id = $system_id AND kind = $kind
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$system_id", scopedSystemId);
        cmd.Parameters.AddWithValue("$kind", kind.ToWire());

        var raw = (string?)await cmd.ExecuteScalarAsync(cancellationToken);
        return raw is null ? null : new ImportOperationId(ParseOperationId(raw));
    }

    public async Task<IReadOnlyList<ImportOperationSnapshot>> GetStaleRunningAsync(
        TimeSpan olderThan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cutoffMs = NowMs() - (long)olderThan.TotalMilliseconds;
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT system_id, operation_id, kind, status, started_at, finished_at,
                   alter_count, error_code, error_message, idempotency_key
            FROM import_operations
            WHERE status = $status AND started_at < $cutoff
            """;
        cmd.Parameters.AddWithValue("$status", ImportOperationStatus.Running.ToWire());
        cmd.Parameters.AddWithValue("$cutoff", cutoffMs);

        var list = new List<ImportOperationSnapshot>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(MapRow(reader));
        }

        return list;
    }

    private static async Task ReleaseSlotAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string scopedSystemId,
        string kindWire,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var delete = connection.CreateCommand();
        delete.Transaction = tx;
        delete.CommandText = """
            DELETE FROM active_import_by_system
            WHERE system_id = $system_id
              AND kind = $kind
              AND operation_id = $operation_id
            """;
        delete.Parameters.AddWithValue("$system_id", scopedSystemId);
        delete.Parameters.AddWithValue("$kind", kindWire);
        delete.Parameters.AddWithValue("$operation_id", FormatOperationId(operationId));
        await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ImportOperationSnapshot MapRow(SqliteDataReader reader)
    {
        var statusText = reader.GetString(3);
        var status = statusText.TryParseWire<ImportOperationStatus>(out var parsed)
            ? parsed
            : ImportOperationStatus.Queued;

        var kindText = reader.GetString(2);
        if (!kindText.TryParseWire<ImportOperationKind>(out var kind))
        {
            throw new InvalidOperationException(
                $"[import-ops] Encountered unknown kind '{kindText}' in import_operations row. " +
                "Data schema drift — add the new value to ImportOperationKind before rolling this migration.");
        }

        var startedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4));
        DateTimeOffset? finishedAt = reader.IsDBNull(5)
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5));

        ImportErrorCode? errorCode = null;
        if (!reader.IsDBNull(7))
        {
            var errorCodeText = reader.GetString(7);
            if (errorCodeText.TryParseWire<ImportErrorCode>(out var parsedError))
            {
                errorCode = parsedError;
            }
        }

        return new ImportOperationSnapshot(
            SqliteStorageKeys.ToWireSystemId(reader.GetString(0)),
            new ImportOperationId(ParseOperationId(reader.GetString(1))),
            kind,
            status,
            startedAt,
            finishedAt,
            reader.IsDBNull(6) ? null : (int)reader.GetInt64(6),
            errorCode,
            reader.IsDBNull(8) ? null : reader.GetString(8),
            new IdempotencyKey(reader.GetString(9)));
    }

    private long NowMs() => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    private static string FormatOperationId(Guid id) => id.ToString("N");

    private static Guid ParseOperationId(string hex) => Guid.ParseExact(hex, "N");
}
