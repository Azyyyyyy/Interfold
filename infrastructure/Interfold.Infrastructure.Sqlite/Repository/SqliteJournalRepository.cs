using Interfold.Journals.Contracts.Ids;
using Interfold.Journals.Contracts.Models.Commands;
using Interfold.Journals.Contracts.Models.Read;
using Interfold.Journals.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Domain.Abstractions;
using Microsoft.Data.Sqlite;

namespace Interfold.Infrastructure.Sqlite.Repository;

public sealed class SqliteJournalRepository(
    ISqliteConnectionFactory connectionFactory,
    IRegionContext regionContext,
    TimeProvider? timeProvider = null) : IJournalRepository
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<EntryId?> CreateGlobalAsync(
        SystemId systemId,
        CreateGlobalJournalEntryCommand command,
        CancellationToken cancellationToken = default)
    {
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        var entryId = Guid.NewGuid();
        var nowMs = NowMs();

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var commandSql = connection.CreateCommand();
        commandSql.CommandText = """
            INSERT INTO global_journals
                (user_id, id, title, content, color, pinned, locked, inserted_at, updated_at)
            VALUES
                ($user_id, $id, $title, NULL, NULL, 0, 0, $inserted_at, $updated_at)
            """;
        commandSql.Parameters.AddWithValue("$user_id", userId);
        commandSql.Parameters.AddWithValue("$id", FormatEntryId(entryId));
        commandSql.Parameters.AddWithValue("$title", command.Title);
        commandSql.Parameters.AddWithValue("$inserted_at", nowMs);
        commandSql.Parameters.AddWithValue("$updated_at", nowMs);
        await commandSql.ExecuteNonQueryAsync(cancellationToken);

        return new EntryId(entryId);
    }

    public async Task<bool> ExistsGlobalAsync(
        SystemId systemId,
        EntryId entryId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        return await ExistsGlobalCoreAsync(connection, systemId, entryId, cancellationToken);
    }

    public async Task<bool> UpdateGlobalAsync(
        SystemId systemId,
        UpdateGlobalJournalEntryCommand command,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        if (!await ExistsGlobalCoreAsync(connection, systemId, command.EntryId, cancellationToken))
        {
            return false;
        }

        if (command.Title is null && command.Content is null && command.Color is null)
        {
            return true;
        }

        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE global_journals SET
                title = COALESCE($title, title),
                content = COALESCE($content, content),
                color = COALESCE($color, color),
                updated_at = $updated_at
            WHERE user_id = $user_id AND id = $id
            """;
        cmd.Parameters.AddWithValue("$title", (object?)command.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$content", (object?)command.Content ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$color", command.Color is { } c ? c.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$updated_at", NowMs());
        cmd.Parameters.AddWithValue("$user_id", userId);
        cmd.Parameters.AddWithValue("$id", FormatEntryId(command.EntryId.Value));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteGlobalAsync(
        SystemId systemId,
        EntryId entryId,
        CancellationToken cancellationToken = default)
    {
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        if (!await ExistsGlobalCoreAsync(connection, systemId, entryId, cancellationToken))
        {
            return false;
        }

        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var deleteAlters = connection.CreateCommand())
            {
                deleteAlters.Transaction = tx;
                deleteAlters.CommandText = """
                    DELETE FROM global_journal_alters
                    WHERE user_id = $user_id AND global_journal_id = $id
                    """;
                deleteAlters.Parameters.AddWithValue("$user_id", userId);
                deleteAlters.Parameters.AddWithValue("$id", FormatEntryId(entryId.Value));
                await deleteAlters.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var deleteEntry = connection.CreateCommand())
            {
                deleteEntry.Transaction = tx;
                deleteEntry.CommandText = """
                    DELETE FROM global_journals
                    WHERE user_id = $user_id AND id = $id
                    """;
                deleteEntry.Parameters.AddWithValue("$user_id", userId);
                deleteEntry.Parameters.AddWithValue("$id", FormatEntryId(entryId.Value));
                await deleteEntry.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public Task<bool> SetGlobalLockedAsync(
        SystemId systemId,
        EntryId entryId,
        bool locked,
        CancellationToken cancellationToken = default)
        => SetGlobalFlagAsync(systemId, entryId, "locked", locked, cancellationToken);

    public Task<bool> SetGlobalPinnedAsync(
        SystemId systemId,
        EntryId entryId,
        bool pinned,
        CancellationToken cancellationToken = default)
        => SetGlobalFlagAsync(systemId, entryId, "pinned", pinned, cancellationToken);

    public async Task<bool> AttachGlobalAlterAsync(
        SystemId systemId,
        EntryId entryId,
        AlterId alterId,
        CancellationToken cancellationToken = default)
    {
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        if (!await ExistsGlobalCoreAsync(connection, systemId, entryId, cancellationToken))
        {
            return false;
        }

        var nowMs = NowMs();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO global_journal_alters
                (user_id, global_journal_id, alter_id, inserted_at, updated_at)
            VALUES
                ($user_id, $id, $alter_id, $inserted_at, $updated_at)
            ON CONFLICT(user_id, global_journal_id, alter_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("$user_id", userId);
        cmd.Parameters.AddWithValue("$id", FormatEntryId(entryId.Value));
        cmd.Parameters.AddWithValue("$alter_id", alterId.Value);
        cmd.Parameters.AddWithValue("$inserted_at", nowMs);
        cmd.Parameters.AddWithValue("$updated_at", nowMs);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DetachGlobalAlterAsync(
        SystemId systemId,
        EntryId entryId,
        AlterId alterId,
        CancellationToken cancellationToken = default)
    {
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM global_journal_alters
            WHERE user_id = $user_id
              AND global_journal_id = $id
              AND alter_id = $alter_id
            """;
        cmd.Parameters.AddWithValue("$user_id", userId);
        cmd.Parameters.AddWithValue("$id", FormatEntryId(entryId.Value));
        cmd.Parameters.AddWithValue("$alter_id", alterId.Value);
        var removed = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return removed > 0;
    }

    public async Task<EntryId?> CreateAlterAsync(
        SystemId systemId,
        CreateAlterJournalEntryCommand command,
        CancellationToken cancellationToken = default)
    {
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        var entryId = Guid.NewGuid();
        var atMs = command.CreatedAt.ToUnixTimeMilliseconds();

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO alter_journals
                (user_id, id, alter_id, title, content, color, pinned, locked, inserted_at, updated_at)
            VALUES
                ($user_id, $id, $alter_id, $title, NULL, NULL, 0, 0, $inserted_at, $updated_at)
            """;
        cmd.Parameters.AddWithValue("$user_id", userId);
        cmd.Parameters.AddWithValue("$id", FormatEntryId(entryId));
        cmd.Parameters.AddWithValue("$alter_id", command.AlterId.Value);
        cmd.Parameters.AddWithValue("$title", command.Title);
        cmd.Parameters.AddWithValue("$inserted_at", atMs);
        cmd.Parameters.AddWithValue("$updated_at", atMs);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        return new EntryId(entryId);
    }

    public async Task<AlterJournalRef?> GetAlterRefAsync(
        SystemId systemId,
        EntryId entryId,
        CancellationToken cancellationToken = default)
    {
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, alter_id
            FROM alter_journals
            WHERE user_id = $user_id AND id = $id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$user_id", userId);
        cmd.Parameters.AddWithValue("$id", FormatEntryId(entryId.Value));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AlterJournalRef(
            new EntryId(ParseEntryId(reader.GetString(0))),
            new AlterId((short)reader.GetInt64(1)));
    }

    public async Task<bool> UpdateAlterAsync(
        SystemId systemId,
        UpdateAlterJournalEntryCommand command,
        CancellationToken cancellationToken = default)
    {
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        if (command.Title is null && command.Content is null && command.Color is null)
        {
            return await ExistsAlterAsync(connection, userId, command.EntryId, cancellationToken);
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE alter_journals SET
                title = COALESCE($title, title),
                content = COALESCE($content, content),
                color = COALESCE($color, color),
                updated_at = $updated_at
            WHERE user_id = $user_id AND id = $id
            """;
        cmd.Parameters.AddWithValue("$title", (object?)command.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$content", (object?)command.Content ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$color", command.Color is { } c ? c.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$updated_at", command.UpdatedAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$user_id", userId);
        cmd.Parameters.AddWithValue("$id", FormatEntryId(command.EntryId.Value));
        var updated = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return updated > 0;
    }

    public async Task<bool> DeleteAlterAsync(
        SystemId systemId,
        EntryId entryId,
        CancellationToken cancellationToken = default)
    {
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM alter_journals
            WHERE user_id = $user_id AND id = $id
            """;
        cmd.Parameters.AddWithValue("$user_id", userId);
        cmd.Parameters.AddWithValue("$id", FormatEntryId(entryId.Value));
        var removed = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return removed > 0;
    }

    public async Task<int> DeleteAllForAlterAsync(
        SystemId systemId,
        AlterId alterId,
        CancellationToken cancellationToken = default)
    {
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            int removed;
            await using (var deleteEntries = connection.CreateCommand())
            {
                deleteEntries.Transaction = tx;
                deleteEntries.CommandText = """
                    DELETE FROM alter_journals
                    WHERE user_id = $user_id AND alter_id = $alter_id
                    """;
                deleteEntries.Parameters.AddWithValue("$user_id", userId);
                deleteEntries.Parameters.AddWithValue("$alter_id", alterId.Value);
                removed = await deleteEntries.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var detach = connection.CreateCommand())
            {
                detach.Transaction = tx;
                detach.CommandText = """
                    DELETE FROM global_journal_alters
                    WHERE user_id = $user_id AND alter_id = $alter_id
                    """;
                detach.Parameters.AddWithValue("$user_id", userId);
                detach.Parameters.AddWithValue("$alter_id", alterId.Value);
                await detach.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            return removed;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public Task<bool> SetAlterLockedAsync(
        SystemId systemId,
        EntryId entryId,
        bool locked,
        CancellationToken cancellationToken = default)
        => SetAlterFlagAsync(systemId, entryId, "locked", locked, cancellationToken);

    public Task<bool> SetAlterPinnedAsync(
        SystemId systemId,
        EntryId entryId,
        bool pinned,
        CancellationToken cancellationToken = default)
        => SetAlterFlagAsync(systemId, entryId, "pinned", pinned, cancellationToken);

    public async Task<IReadOnlyList<AlterJournalReadModel>> ListAlterAsync(
        SystemId systemId,
        AlterId alterId,
        CancellationToken cancellationToken = default)
    {
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, user_id, alter_id, title, content, color, pinned, locked, inserted_at, updated_at
            FROM alter_journals
            WHERE user_id = $user_id AND alter_id = $alter_id
            ORDER BY inserted_at DESC
            """;
        cmd.Parameters.AddWithValue("$user_id", userId);
        cmd.Parameters.AddWithValue("$alter_id", alterId.Value);

        var list = new List<AlterJournalReadModel>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(MapAlterJournal(reader));
        }

        return list;
    }

    public async Task<AlterJournalReadModel?> GetAlterAsync(
        SystemId systemId,
        EntryId entryId,
        CancellationToken cancellationToken = default)
    {
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, user_id, alter_id, title, content, color, pinned, locked, inserted_at, updated_at
            FROM alter_journals
            WHERE user_id = $user_id AND id = $id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$user_id", userId);
        cmd.Parameters.AddWithValue("$id", FormatEntryId(entryId.Value));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return MapAlterJournal(reader);
    }

    public async Task<IReadOnlyList<JournalReadModel>> ListGlobalAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, user_id, title, content, color, pinned, locked, inserted_at, updated_at
            FROM global_journals
            WHERE user_id = $user_id
            """;
        cmd.Parameters.AddWithValue("$user_id", userId);

        var drafts = new List<(string IdHex, JournalReadModel Model)>();

        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var idHex = reader.GetString(0);
                drafts.Add((idHex, MapJournalWithoutAlters(reader)));
            }
        }

        var result = new List<JournalReadModel>(drafts.Count);
        foreach (var (idHex, draft) in drafts)
        {
            var alterIds = await LoadAlterIdsAsync(connection, userId, idHex, cancellationToken);
            result.Add(draft with { Alters = alterIds });
        }

        // Sort by wire form (lowercase "N" hex); Guid.CompareTo would reorder differently.
        return result
            .OrderByDescending(e => e.Id.Value.ToString("N"), StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<JournalReadModel?> GetGlobalAsync(
        SystemId systemId,
        EntryId entryId,
        CancellationToken cancellationToken = default)
    {
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        var idHex = FormatEntryId(entryId.Value);
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, user_id, title, content, color, pinned, locked, inserted_at, updated_at
            FROM global_journals
            WHERE user_id = $user_id AND id = $id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$user_id", userId);
        cmd.Parameters.AddWithValue("$id", idHex);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var draft = MapJournalWithoutAlters(reader);
        await reader.DisposeAsync();

        var alterIds = await LoadAlterIdsAsync(connection, userId, idHex, cancellationToken);
        return draft with { Alters = alterIds };
    }

    private async Task<bool> SetGlobalFlagAsync(
        SystemId systemId,
        EntryId entryId,
        string column,
        bool value,
        CancellationToken cancellationToken)
    {
        // column is a closed set from callers (locked/pinned) — never bind user input as an identifier.
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        if (!await ExistsGlobalCoreAsync(connection, systemId, entryId, cancellationToken))
        {
            return false;
        }

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            UPDATE global_journals
            SET {column} = $value, updated_at = $updated_at
            WHERE user_id = $user_id AND id = $id
            """;
        cmd.Parameters.AddWithValue("$value", value ? 1 : 0);
        cmd.Parameters.AddWithValue("$updated_at", NowMs());
        cmd.Parameters.AddWithValue("$user_id", userId);
        cmd.Parameters.AddWithValue("$id", FormatEntryId(entryId.Value));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    private async Task<bool> SetAlterFlagAsync(
        SystemId systemId,
        EntryId entryId,
        string column,
        bool value,
        CancellationToken cancellationToken)
    {
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            UPDATE alter_journals
            SET {column} = $value, updated_at = $updated_at
            WHERE user_id = $user_id AND id = $id
            """;
        cmd.Parameters.AddWithValue("$value", value ? 1 : 0);
        cmd.Parameters.AddWithValue("$updated_at", NowMs());
        cmd.Parameters.AddWithValue("$user_id", userId);
        cmd.Parameters.AddWithValue("$id", FormatEntryId(entryId.Value));
        var updated = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return updated > 0;
    }

    private async Task<bool> ExistsGlobalCoreAsync(
        SqliteConnection connection,
        SystemId systemId,
        EntryId entryId,
        CancellationToken cancellationToken)
    {
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM global_journals
            WHERE user_id = $user_id AND id = $id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$user_id", userId);
        cmd.Parameters.AddWithValue("$id", FormatEntryId(entryId.Value));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken);
    }

    private static async Task<bool> ExistsAlterAsync(
        SqliteConnection connection,
        string userId,
        EntryId entryId,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM alter_journals
            WHERE user_id = $user_id AND id = $id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$user_id", userId);
        cmd.Parameters.AddWithValue("$id", FormatEntryId(entryId.Value));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<AlterId>> LoadAlterIdsAsync(
        SqliteConnection connection,
        string userId,
        string entryIdHex,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT alter_id
            FROM global_journal_alters
            WHERE user_id = $user_id AND global_journal_id = $id
            """;
        cmd.Parameters.AddWithValue("$user_id", userId);
        cmd.Parameters.AddWithValue("$id", entryIdHex);

        var list = new List<AlterId>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new AlterId((short)reader.GetInt64(0)));
        }

        return list;
    }

    private static JournalReadModel MapJournalWithoutAlters(SqliteDataReader reader)
    {
        return new JournalReadModel(
            new EntryId(ParseEntryId(reader.GetString(0))),
            SqliteStorageKeys.ToWireSystemId(reader.GetString(1)),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            HexColor.FromNullable(reader.IsDBNull(4) ? null : reader.GetString(4)),
            reader.GetInt64(6) != 0,
            reader.GetInt64(5) != 0,
            FromUnixMs(reader.GetInt64(7)),
            FromUnixMs(reader.GetInt64(8)),
            Array.Empty<AlterId>());
    }

    private static AlterJournalReadModel MapAlterJournal(SqliteDataReader reader)
    {
        return new AlterJournalReadModel(
            new EntryId(ParseEntryId(reader.GetString(0))),
            SqliteStorageKeys.ToWireSystemId(reader.GetString(1)),
            new AlterId((short)reader.GetInt64(2)),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            HexColor.FromNullable(reader.IsDBNull(5) ? null : reader.GetString(5)),
            reader.GetInt64(7) != 0,
            reader.GetInt64(6) != 0,
            FromUnixMs(reader.GetInt64(8)),
            FromUnixMs(reader.GetInt64(9)));
    }

    private long NowMs() => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    private static string FormatEntryId(Guid id) => id.ToString("N");

    private static Guid ParseEntryId(string hex) => Guid.ParseExact(hex, "N");

    private static DateTime FromUnixMs(long ms)
        => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
}
