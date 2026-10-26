using Dapper;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Infrastructure.Sqlite;
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
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await LoadFieldsAsync(connection, systemId);
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
        var ids = await CreateManyAsync(
            systemId,
            [new SettingsFieldInsert(name, type, securityLevel, locked, insertedAtUtc)],
            cancellationToken);
        return ids.Count == 0 ? null : ids[0];
    }

    public async Task<IReadOnlyList<FieldId>> CreateManyAsync(
        SystemId systemId,
        IReadOnlyList<SettingsFieldInsert> fields,
        CancellationToken cancellationToken = default)
    {
        if (fields.Count == 0)
            return [];

        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using var work = await SqliteWork.OpenAsync(_connectionFactory, cancellationToken);
        var nextIndex = await work.Connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM settings_fields WHERE system_id = @system_id",
            new { system_id = systemId },
            work.Transaction);

        var ids = new FieldId[fields.Count];
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            var fieldId = Guid.NewGuid();
            var insertedAtMs = new DateTimeOffset(DateTime.SpecifyKind(field.InsertedAtUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            await work.Connection.ExecuteAsync(
                """
                INSERT INTO settings_fields (
                    system_id, id, name, type, security_level, locked, idx, inserted_at, updated_at)
                VALUES (
                    @system_id, @id, @name, @type, @security_level, @locked, @idx, @inserted_at, @updated_at)
                """,
                new
                {
                    system_id = systemId,
                    id = fieldId.ToString("N"),
                    name = field.Name,
                    type = (short)field.Type,
                    security_level = (short)field.SecurityLevel,
                    locked = field.Locked ? 1 : 0,
                    idx = (int)nextIndex + i,
                    inserted_at = insertedAtMs,
                    updated_at = nowMs,
                },
                work.Transaction);
            ids[i] = new FieldId(fieldId);
        }

        await work.CommitAsync(cancellationToken);
        return ids;
    }

    public async Task<bool> UpdateAsync(
        SystemId systemId,
        FieldId fieldId,
        string? name,
        VisibilityLevel? securityLevel,
        bool? locked,
        CancellationToken cancellationToken = default)
    {
        var fieldHex = fieldId.Value.ToString("N");
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QueryFirstOrDefaultAsync<FieldRow>(
            """
            SELECT id AS IdHex, name AS Name, type AS Type, security_level AS SecurityLevel,
                   locked AS Locked, idx AS Idx, inserted_at AS InsertedAtMs, updated_at AS UpdatedAtMs
            FROM settings_fields
            WHERE system_id = @system_id AND id = @id
            """,
            new { system_id = systemId, id = fieldHex });
        if (row is null)
        {
            return false;
        }

        if (name is null && securityLevel is null && locked is null)
        {
            return true;
        }

        if (name is not null)
            row.Name = name;
        if (securityLevel is not null)
            row.SecurityLevel = (short)securityLevel.Value;
        if (locked is not null)
            row.Locked = locked.Value ? 1 : 0;
        row.UpdatedAtMs = nowMs;

        var updated = await connection.ExecuteAsync(
            """
            UPDATE settings_fields
            SET name = @name, type = @type, security_level = @security_level,
                locked = @locked, idx = @idx, updated_at = @updated_at
            WHERE system_id = @system_id AND id = @id
            """,
            new
            {
                system_id = systemId,
                id = fieldHex,
                name = row.Name,
                type = row.Type,
                security_level = row.SecurityLevel,
                locked = row.Locked,
                idx = row.Idx,
                updated_at = row.UpdatedAtMs,
            });
        return updated > 0;
    }

    public async Task<bool> DeleteAsync(
        SystemId systemId,
        FieldId fieldId,
        CancellationToken cancellationToken = default)
    {
        var fieldHex = fieldId.Value.ToString("N");

        await using var work = await SqliteWork.OpenAsync(_connectionFactory, cancellationToken);

        if (!await FieldExistsAsync(work.Connection, work.Transaction, systemId, fieldHex))
            return false;

        var removed = await work.Connection.ExecuteAsync(
            """
            DELETE FROM settings_fields
            WHERE system_id = @system_id AND id = @id
            """,
            new { system_id = systemId, id = fieldHex },
            work.Transaction);

        await ReindexAsync(work.Connection, work.Transaction, systemId);
        await work.CommitAsync(cancellationToken);
        return removed > 0;
    }

    public async Task<bool> RelocateAsync(
        SystemId systemId,
        FieldId fieldId,
        int index,
        CancellationToken cancellationToken = default)
    {
        var fieldHex = fieldId.Value.ToString("N");
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        var rows = await LoadFieldsAsync(connection, systemId, tx);
        var currentIndex = rows.FindIndex(r => r.IdHex == fieldHex);
        if (currentIndex < 0)
        {
            return false;
        }

        var field = rows[currentIndex];
        rows.RemoveAt(currentIndex);
        var boundedIndex = Math.Max(0, Math.Min(index, rows.Count));
        field.UpdatedAtMs = nowMs;
        rows.Insert(boundedIndex, field);

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
                    system_id = systemId,
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
        SystemId systemId)
    {
        var rows = await LoadFieldsAsync(connection, systemId, tx);
        for (var i = 0; i < rows.Count; i++)
        {
            await connection.ExecuteAsync(
                """
                UPDATE settings_fields
                SET idx = @idx
                WHERE system_id = @system_id AND id = @id
                """,
                new { idx = i, system_id = systemId, id = rows[i].IdHex },
                tx);
        }
    }

    private static async Task<bool> FieldExistsAsync(
        SqliteConnection connection,
        IDbTransaction? tx,
        SystemId systemId,
        string fieldHex)
    {
        var found = await connection.ExecuteScalarAsync<long?>(
            """
            SELECT 1 FROM settings_fields
            WHERE system_id = @system_id AND id = @id
            LIMIT 1
            """,
            new { system_id = systemId, id = fieldHex },
            tx);
        return found is not null;
    }

    private static async Task<List<FieldRow>> LoadFieldsAsync(
        SqliteConnection connection,
        SystemId systemId,
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
            new { system_id = systemId },
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

}
