using Dapper;
using Interfold.Settings.Contracts.Ids;
using Interfold.Settings.Contracts.Models.ImportOperations;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Microsoft.Data.Sqlite;
using System.Data.Common;

namespace Interfold.Infrastructure.Sqlite.Repository;

/// <summary>SQLite port of <see cref="IImportOperationRepository"/>. The per-system mutex
/// is <c>INSERT … ON CONFLICT DO NOTHING</c> on <c>active_import_by_system</c>; terminal
/// transitions release with a conditional <c>DELETE … AND operation_id = ?</c>.</summary>
public sealed class SqliteImportOperationRepository : IImportOperationRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _clock;

    public SqliteImportOperationRepository(ISqliteConnectionFactory connectionFactory, TimeProvider timeProvider)
    {
        _connectionFactory = connectionFactory;
        _clock = timeProvider;
    }

    public async Task<ImportOperationClaim> TryClaimAsync(
        SystemId systemId,
        ImportOperationKind kind,
        IdempotencyKey idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var kindWire = kind.ToWire();
        var newOperationId = Guid.NewGuid();
        var nowMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var applied = await connection.ExecuteAsync(
                """
                INSERT INTO active_import_by_system (system_id, kind, operation_id, started_at)
                VALUES (@system_id, @kind, @operation_id, @started_at)
                ON CONFLICT(system_id, kind) DO NOTHING
                """,
                new
                {
                    system_id = systemId.Value,
                    kind = kindWire,
                    operation_id = FormatOperationId(newOperationId),
                    started_at = nowMs,
                },
                tx);
            if (applied == 0)
            {
                var existingRaw = await connection.QueryFirstOrDefaultAsync<string>(
                    """
                    SELECT operation_id
                    FROM active_import_by_system
                    WHERE system_id = @system_id AND kind = @kind
                    LIMIT 1
                    """,
                    new { system_id = systemId.Value, kind = kindWire },
                    tx);
                await tx.CommitAsync(cancellationToken);

                if (existingRaw is null)
                {
                    throw new InvalidOperationException(
                        "[import-ops] Claim conflicted but no active row was found — unexpected race.");
                }

                return new ImportOperationClaim(new ImportOperationId(ParseOperationId(existingRaw)), IsNew: false);
            }

            await connection.ExecuteAsync(
                """
                INSERT INTO import_operations
                    (system_id, operation_id, kind, status, started_at, idempotency_key)
                VALUES
                    (@system_id, @operation_id, @kind, @status, @started_at, @idempotency_key)
                """,
                new
                {
                    system_id = systemId.Value,
                    operation_id = FormatOperationId(newOperationId),
                    kind = kindWire,
                    status = ImportOperationStatus.Queued.ToWire(),
                    started_at = nowMs,
                    idempotency_key = idempotencyKey.Value,
                },
                tx);

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

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            UPDATE import_operations
            SET status = @running
            WHERE system_id = @system_id
              AND operation_id = @operation_id
              AND status = @queued
            """,
            new
            {
                running = ImportOperationStatus.Running.ToWire(),
                system_id = systemId.Value,
                operation_id = FormatOperationId(operationId.Value),
                queued = ImportOperationStatus.Queued.ToWire(),
            });
    }

    public async Task MarkSucceededAsync(
        SystemId systemId,
        ImportOperationId operationId,
        ImportOperationKind kind,
        int alterCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var nowMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await connection.ExecuteAsync(
                """
                UPDATE import_operations
                SET status = @status, finished_at = @finished_at, alter_count = @alter_count
                WHERE system_id = @system_id AND operation_id = @operation_id
                """,
                new
                {
                    status = ImportOperationStatus.Succeeded.ToWire(),
                    finished_at = nowMs,
                    alter_count = alterCount,
                    system_id = systemId.Value,
                    operation_id = FormatOperationId(operationId.Value),
                },
                tx);

            await ReleaseSlotAsync(connection, tx, systemId, kind.ToWire(), operationId.Value, cancellationToken);
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

        var nowMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await connection.ExecuteAsync(
                """
                UPDATE import_operations
                SET status = @status,
                    finished_at = @finished_at,
                    error_code = @error_code,
                    error_message = @error_message
                WHERE system_id = @system_id AND operation_id = @operation_id
                """,
                new
                {
                    status = ImportOperationStatus.Failed.ToWire(),
                    finished_at = nowMs,
                    error_code = errorCode.ToWire(),
                    error_message = errorMessage,
                    system_id = systemId.Value,
                    operation_id = FormatOperationId(operationId.Value),
                },
                tx);

            await ReleaseSlotAsync(connection, tx, systemId, kind.ToWire(), operationId.Value, cancellationToken);
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

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QueryFirstOrDefaultAsync<ImportOperationRow>(
            """
            SELECT system_id AS SystemId, operation_id AS OperationId, kind AS Kind, status AS Status,
                   started_at AS StartedAt, finished_at AS FinishedAt,
                   alter_count AS AlterCount, error_code AS ErrorCode, error_message AS ErrorMessage,
                   idempotency_key AS IdempotencyKey
            FROM import_operations
            WHERE system_id = @system_id AND operation_id = @operation_id
            LIMIT 1
            """,
            new
            {
                system_id = systemId.Value,
                operation_id = FormatOperationId(operationId.Value),
            });
        return row is null ? null : MapRow(row);
    }

    public async Task<ImportOperationId?> GetActiveOperationIdAsync(
        SystemId systemId,
        ImportOperationKind kind,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var raw = await connection.QueryFirstOrDefaultAsync<string>(
            """
            SELECT operation_id
            FROM active_import_by_system
            WHERE system_id = @system_id AND kind = @kind
            LIMIT 1
            """,
            new { system_id = systemId.Value, kind = kind.ToWire() });
        return raw is null ? null : new ImportOperationId(ParseOperationId(raw));
    }

    public async Task<IReadOnlyList<ImportOperationSnapshot>> GetStaleRunningAsync(
        TimeSpan olderThan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cutoffMs = _clock.GetUtcNow().ToUnixTimeMilliseconds() - (long)olderThan.TotalMilliseconds;
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ImportOperationRow>(
            """
            SELECT system_id AS SystemId, operation_id AS OperationId, kind AS Kind, status AS Status,
                   started_at AS StartedAt, finished_at AS FinishedAt,
                   alter_count AS AlterCount, error_code AS ErrorCode, error_message AS ErrorMessage,
                   idempotency_key AS IdempotencyKey
            FROM import_operations
            WHERE status = @status AND started_at < @cutoff
            """,
            new { status = ImportOperationStatus.Running.ToWire(), cutoff = cutoffMs });
        return rows.Select(MapRow).ToArray();
    }

    private static Task ReleaseSlotAsync(
        SqliteConnection connection,
        DbTransaction tx,
        SystemId systemId,
        string kindWire,
        Guid operationId,
        CancellationToken cancellationToken)
        => connection.ExecuteAsync(
            """
            DELETE FROM active_import_by_system
            WHERE system_id = @system_id
              AND kind = @kind
              AND operation_id = @operation_id
            """,
            new
            {
                system_id = systemId.Value,
                kind = kindWire,
                operation_id = FormatOperationId(operationId),
            },
            tx);

    private static ImportOperationSnapshot MapRow(ImportOperationRow row)
    {
        var status = row.Status.TryParseWire<ImportOperationStatus>(out var parsed)
            ? parsed
            : ImportOperationStatus.Queued;

        if (!row.Kind.TryParseWire<ImportOperationKind>(out var kind))
        {
            throw new InvalidOperationException(
                $"[import-ops] Encountered unknown kind '{row.Kind}' in import_operations row. " +
                "Data schema drift — add the new value to ImportOperationKind before rolling this migration.");
        }

        var startedAt = DateTimeOffset.FromUnixTimeMilliseconds(row.StartedAt);
        DateTimeOffset? finishedAt = row.FinishedAt is null
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(row.FinishedAt.Value);

        ImportErrorCode? errorCode = null;
        if (row.ErrorCode is { } errorCodeText &&
            errorCodeText.TryParseWire<ImportErrorCode>(out var parsedError))
        {
            errorCode = parsedError;
        }

        return new ImportOperationSnapshot(
            new SystemId(row.SystemId),
            new ImportOperationId(ParseOperationId(row.OperationId)),
            kind,
            status,
            startedAt,
            finishedAt,
            row.AlterCount is null ? null : (int)row.AlterCount.Value,
            errorCode,
            row.ErrorMessage,
            new IdempotencyKey(row.IdempotencyKey));
    }


    private static string FormatOperationId(Guid id) => id.ToString("N");

    private static Guid ParseOperationId(string hex) => Guid.ParseExact(hex, "N");

    private sealed record ImportOperationRow(
        string SystemId,
        string OperationId,
        string Kind,
        string Status,
        long StartedAt,
        long? FinishedAt,
        long? AlterCount,
        string? ErrorCode,
        string? ErrorMessage,
        string IdempotencyKey);
}
