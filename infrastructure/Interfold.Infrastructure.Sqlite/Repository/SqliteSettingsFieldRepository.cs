using Dapper;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Shared.Domain.Abstractions.Repository;
using Microsoft.Data.Sqlite;
using System.Data;

namespace Interfold.Infrastructure.Sqlite.Repository;

public sealed class SqliteSettingsFieldRepository : ISettingsFieldRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;

    public SqliteSettingsFieldRepository(
        ISqliteConnectionFactory connectionFactory,
        TimeProvider timeProvider)
    {
        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<SettingsFieldReadModel>> ListAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.Persist(systemId);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await LoadFieldsAsync(connection, systemKey);
        return rows
            .OrderBy(r => r.Idx)
            .Select(Map)
            .ToArray();
    }

    public async Task<FieldId?> CreateAsync(
        SystemId systemId,
        string name,
        FieldType type,
        VisibilityLevel securityLevel,
        bool locked,
        DateTime insertedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.Persist(systemId);
        var fieldId = Guid.NewGuid();
        var fieldHex = fieldId.ToString("N");
        var insertedAtMs = new DateTimeOffset(DateTime.SpecifyKind(insertedAtUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        var nextIndex = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM settings_fields WHERE system_id = @system_id",
            new { system_id = systemKey },
            tx);

        await connection.ExecuteAsync(
            """
            INSERT INTO settings_fields (
                system_id, id, name, type, security_level, locked, idx, inserted_at, updated_at)
            VALUES (
                @system_id, @id, @name, @type, @security_level, @locked, @idx, @inserted_at, @updated_at)
            """,
            new
            {
                system_id = systemKey,
                id = fieldHex,
                name,
                type = (short)type,
                security_level = (short)securityLevel,
                locked = locked ? 1 : 0,
                idx = (int)nextIndex,
                inserted_at = insertedAtMs,
                updated_at = nowMs,
            },
            tx);

        await tx.CommitAsync(cancellationToken);
        return new FieldId(fieldId);
    }

    public async Task<bool> UpdateAsync(
        SystemId systemId,
        FieldId fieldId,
        string? name,
        VisibilityLevel? securityLevel,
        bool? locked,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.Persist(systemId);
        var fieldHex = fieldId.Value.ToString("N");
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        if (!await FieldExistsAsync(connection, tx: null, systemKey, fieldHex))
        {
            return false;
        }

        var sets = new List<string> { "updated_at = @updated_at" };
        var parameters = new DynamicParameters();
        parameters.Add("updated_at", nowMs);
        parameters.Add("system_id", systemKey);
        parameters.Add("id", fieldHex);

        if (name is not null)
        {
            sets.Add("name = @name");
            parameters.Add("name", name);
        }

        if (securityLevel is not null)
        {
            sets.Add("security_level = @security_level");
            parameters.Add("security_level", (short)securityLevel.Value);
        }

        if (locked is not null)
        {
            sets.Add("locked = @locked");
            parameters.Add("locked", locked.Value ? 1 : 0);
        }

        var updated = await connection.ExecuteAsync(
            $"""
            UPDATE settings_fields
            SET {string.Join(", ", sets)}
            WHERE system_id = @system_id AND id = @id
            """,
            parameters);
        return updated > 0;
    }

    public async Task<bool> DeleteAsync(
        SystemId systemId,
        FieldId fieldId,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.Persist(systemId);
        var fieldHex = fieldId.Value.ToString("N");

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        if (!await FieldExistsAsync(connection, tx, systemKey, fieldHex))
        {
            return false;
        }

        var removed = await connection.ExecuteAsync(
            """
            DELETE FROM settings_fields
            WHERE system_id = @system_id AND id = @id
            """,
            new { system_id = systemKey, id = fieldHex },
            tx);

        await ReindexAsync(connection, tx, systemKey);
        await tx.CommitAsync(cancellationToken);

        try
        {
            await SqliteAlterRepository.RemoveFieldValuesForSystemAsync(
                _connectionFactory, systemKey, fieldId.Value, cancellationToken);
        }
        catch
        {
            // best-effort cascade, tolerate missing alter_fields table mid-migration
        }

        return removed > 0;
    }

    public async Task<bool> RelocateAsync(
        SystemId systemId,
        FieldId fieldId,
        int index,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.Persist(systemId);
        var fieldHex = fieldId.Value.ToString("N");
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        var rows = await LoadFieldsAsync(connection, systemKey, tx);
        var currentIndex = rows.FindIndex(r => r.IdHex == fieldHex);
        if (currentIndex < 0)
        {
            return false;
        }

        var field = rows[currentIndex];
        rows.RemoveAt(currentIndex);
        var boundedIndex = Math.Max(0, Math.Min(index, rows.Count));
        rows.Insert(boundedIndex, field with { UpdatedAtMs = nowMs });

        for (var i = 0; i < rows.Count; i++)
        {
            await connection.ExecuteAsync(
                """
                UPDATE settings_fields
                SET idx = @idx, updated_at = @updated_at
                WHERE system_id = @system_id AND id = @id
                """,
                new
                {
                    idx = i,
                    updated_at = rows[i].UpdatedAtMs,
                    system_id = systemKey,
                    id = rows[i].IdHex,
                },
                tx);
        }

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task ReindexAsync(
        SqliteConnection connection,
        IDbTransaction tx,
        string systemKey)
    {
        var rows = await LoadFieldsAsync(connection, systemKey, tx);
        for (var i = 0; i < rows.Count; i++)
        {
            await connection.ExecuteAsync(
                """
                UPDATE settings_fields
                SET idx = @idx
                WHERE system_id = @system_id AND id = @id
                """,
                new { idx = i, system_id = systemKey, id = rows[i].IdHex },
                tx);
        }
    }

    private static async Task<bool> FieldExistsAsync(
        SqliteConnection connection,
        IDbTransaction? tx,
        string systemKey,
        string fieldHex)
    {
        var found = await connection.ExecuteScalarAsync<long?>(
            """
            SELECT 1 FROM settings_fields
            WHERE system_id = @system_id AND id = @id
            LIMIT 1
            """,
            new { system_id = systemKey, id = fieldHex },
            tx);
        return found is not null;
    }

    private static async Task<List<FieldRow>> LoadFieldsAsync(
        SqliteConnection connection,
        string systemKey,
        IDbTransaction? tx = null)
    {
        var rows = await connection.QueryAsync<FieldRow>(
            """
            SELECT id AS IdHex, name AS Name, type AS Type, security_level AS SecurityLevel,
                   locked AS Locked, idx AS Idx, inserted_at AS InsertedAtMs, updated_at AS UpdatedAtMs
            FROM settings_fields
            WHERE system_id = @system_id
            ORDER BY idx
            """,
            new { system_id = systemKey },
            tx);

        return rows.ToList();
    }

    private static SettingsFieldReadModel Map(FieldRow row)
        => new(
            new FieldId(Guid.Parse(row.IdHex)),
            row.Name,
            ((short)row.Type).FromCode<FieldType>(),
            ((short)row.SecurityLevel).FromCode<VisibilityLevel>(),
            row.Locked != 0,
            (int)row.Idx,
            DateTimeOffset.FromUnixTimeMilliseconds(row.InsertedAtMs).UtcDateTime);

    private sealed record FieldRow(
        string IdHex,
        string Name,
        long Type,
        long SecurityLevel,
        long Locked,
        long Idx,
        long InsertedAtMs,
        long UpdatedAtMs);
}
