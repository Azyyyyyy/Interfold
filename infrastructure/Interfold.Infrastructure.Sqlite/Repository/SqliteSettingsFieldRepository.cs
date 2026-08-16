using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;
using Microsoft.Data.Sqlite;

namespace Interfold.Infrastructure.Sqlite.Repository;

public sealed class SqliteSettingsFieldRepository : ISettingsFieldRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IRegionContext _regionContext;
    private readonly TimeProvider _timeProvider;

    public SqliteSettingsFieldRepository(
        ISqliteConnectionFactory connectionFactory,
        IRegionContext regionContext,
        TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory;
        _regionContext = regionContext;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<SettingsFieldReadModel>> ListAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await LoadFieldsAsync(connection, systemKey, cancellationToken);
        return rows
            .OrderBy(r => r.Index)
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
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        var fieldId = Guid.NewGuid();
        var fieldHex = fieldId.ToString("N");
        var insertedAtMs = new DateTimeOffset(DateTime.SpecifyKind(insertedAtUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        int nextIndex;
        await using (var countCmd = connection.CreateCommand())
        {
            countCmd.Transaction = tx;
            countCmd.CommandText = "SELECT COUNT(*) FROM settings_fields WHERE system_id = $system_id";
            countCmd.Parameters.AddWithValue("$system_id", systemKey);
            nextIndex = Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken) ?? 0);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO settings_fields (
                    system_id, id, name, type, security_level, locked, idx, inserted_at, updated_at)
                VALUES (
                    $system_id, $id, $name, $type, $security_level, $locked, $idx, $inserted_at, $updated_at)
                """;
            insert.Parameters.AddWithValue("$system_id", systemKey);
            insert.Parameters.AddWithValue("$id", fieldHex);
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$type", type.ToWire());
            insert.Parameters.AddWithValue("$security_level", securityLevel.ToWire());
            insert.Parameters.AddWithValue("$locked", locked ? 1 : 0);
            insert.Parameters.AddWithValue("$idx", nextIndex);
            insert.Parameters.AddWithValue("$inserted_at", insertedAtMs);
            insert.Parameters.AddWithValue("$updated_at", nowMs);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

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
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        var fieldHex = fieldId.Value.ToString("N");
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        if (!await FieldExistsAsync(connection, systemKey, fieldHex, cancellationToken))
        {
            return false;
        }

        var sets = new List<string> { "updated_at = $updated_at" };
        await using var update = connection.CreateCommand();
        update.Parameters.AddWithValue("$updated_at", nowMs);
        update.Parameters.AddWithValue("$system_id", systemKey);
        update.Parameters.AddWithValue("$id", fieldHex);

        if (name is not null)
        {
            sets.Add("name = $name");
            update.Parameters.AddWithValue("$name", name);
        }

        if (securityLevel is not null)
        {
            sets.Add("security_level = $security_level");
            update.Parameters.AddWithValue("$security_level", securityLevel.Value.ToWire());
        }

        if (locked is not null)
        {
            sets.Add("locked = $locked");
            update.Parameters.AddWithValue("$locked", locked.Value ? 1 : 0);
        }

        update.CommandText = $"""
            UPDATE settings_fields
            SET {string.Join(", ", sets)}
            WHERE system_id = $system_id AND id = $id
            """;
        await update.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(
        SystemId systemId,
        FieldId fieldId,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        var fieldHex = fieldId.Value.ToString("N");

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        if (!await FieldExistsAsync(connection, tx, systemKey, fieldHex, cancellationToken))
        {
            return false;
        }

        await using (var deleteField = connection.CreateCommand())
        {
            deleteField.Transaction = tx;
            deleteField.CommandText = """
                DELETE FROM settings_fields
                WHERE system_id = $system_id AND id = $id
                """;
            deleteField.Parameters.AddWithValue("$system_id", systemKey);
            deleteField.Parameters.AddWithValue("$id", fieldHex);
            await deleteField.ExecuteNonQueryAsync(cancellationToken);
        }

        await ReindexAsync(connection, tx, systemKey, cancellationToken);
        await tx.CommitAsync(cancellationToken);

        // Cascade clear alter field values — best-effort if alter tables are present.
        try
        {
            await SqliteAlterRepository.RemoveFieldValuesForSystemAsync(
                _connectionFactory, systemKey, fieldId.Value, cancellationToken);
        }
        catch
        {
            // best-effort cascade, tolerate missing alter_fields table mid-migration
        }

        return true;
    }

    public async Task<bool> RelocateAsync(
        SystemId systemId,
        FieldId fieldId,
        int index,
        CancellationToken cancellationToken = default)
    {
        var systemKey = SqliteStorageKeys.ForSystem(_regionContext, systemId).Value;
        var fieldHex = fieldId.Value.ToString("N");
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var rows = await LoadFieldsAsync(connection, tx, systemKey, cancellationToken);
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
            await using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                UPDATE settings_fields
                SET idx = $idx, updated_at = $updated_at
                WHERE system_id = $system_id AND id = $id
                """;
            update.Parameters.AddWithValue("$idx", i);
            update.Parameters.AddWithValue("$updated_at", rows[i].UpdatedAtMs);
            update.Parameters.AddWithValue("$system_id", systemKey);
            update.Parameters.AddWithValue("$id", rows[i].IdHex);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task ReindexAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string systemKey,
        CancellationToken cancellationToken)
    {
        var rows = await LoadFieldsAsync(connection, tx, systemKey, cancellationToken);
        for (var i = 0; i < rows.Count; i++)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                UPDATE settings_fields
                SET idx = $idx
                WHERE system_id = $system_id AND id = $id
                """;
            update.Parameters.AddWithValue("$idx", i);
            update.Parameters.AddWithValue("$system_id", systemKey);
            update.Parameters.AddWithValue("$id", rows[i].IdHex);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<bool> FieldExistsAsync(
        SqliteConnection connection,
        string systemKey,
        string fieldHex,
        CancellationToken cancellationToken)
        => await FieldExistsAsync(connection, tx: null, systemKey, fieldHex, cancellationToken);

    private static async Task<bool> FieldExistsAsync(
        SqliteConnection connection,
        SqliteTransaction? tx,
        string systemKey,
        string fieldHex,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT 1 FROM settings_fields
            WHERE system_id = $system_id AND id = $id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$system_id", systemKey);
        command.Parameters.AddWithValue("$id", fieldHex);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken);
    }

    private static Task<List<FieldRow>> LoadFieldsAsync(
        SqliteConnection connection,
        string systemKey,
        CancellationToken cancellationToken)
        => LoadFieldsAsync(connection, tx: null, systemKey, cancellationToken);

    private static async Task<List<FieldRow>> LoadFieldsAsync(
        SqliteConnection connection,
        SqliteTransaction? tx,
        string systemKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT id, name, type, security_level, locked, idx, inserted_at, updated_at
            FROM settings_fields
            WHERE system_id = $system_id
            ORDER BY idx
            """;
        command.Parameters.AddWithValue("$system_id", systemKey);

        var rows = new List<FieldRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var typeWire = reader.GetString(2);
            if (!typeWire.TryParseWire(out FieldType type))
            {
                throw new InvalidOperationException($"Corrupt settings_fields type wire value '{typeWire}'.");
            }

            var securityWire = reader.GetString(3);
            if (!securityWire.TryParseWire(out VisibilityLevel securityLevel))
            {
                throw new InvalidOperationException($"Corrupt settings_fields security_level wire value '{securityWire}'.");
            }

            rows.Add(new FieldRow(
                IdHex: reader.GetString(0),
                Name: reader.GetString(1),
                Type: type,
                SecurityLevel: securityLevel,
                Locked: reader.GetInt64(4) != 0,
                Index: Convert.ToInt32(reader.GetInt64(5)),
                InsertedAtMs: reader.GetInt64(6),
                UpdatedAtMs: reader.GetInt64(7)));
        }

        return rows;
    }

    private static SettingsFieldReadModel Map(FieldRow row)
        => new(
            new FieldId(Guid.Parse(row.IdHex)),
            row.Name,
            row.Type,
            row.SecurityLevel,
            row.Locked,
            row.Index,
            DateTimeOffset.FromUnixTimeMilliseconds(row.InsertedAtMs).UtcDateTime);

    private sealed record FieldRow(
        string IdHex,
        string Name,
        FieldType Type,
        VisibilityLevel SecurityLevel,
        bool Locked,
        int Index,
        long InsertedAtMs,
        long UpdatedAtMs);
}
