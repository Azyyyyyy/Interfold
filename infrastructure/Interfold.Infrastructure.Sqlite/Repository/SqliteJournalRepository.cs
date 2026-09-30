using Dapper;
using Interfold.Journals.Contracts.Ids;
using Interfold.Journals.Contracts.Models.Commands;
using Interfold.Journals.Contracts.Models.Read;
using Interfold.Journals.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Microsoft.Data.Sqlite;

namespace Interfold.Infrastructure.Sqlite.Repository;

public sealed class SqliteJournalRepository : IJournalRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _clock;

    public SqliteJournalRepository(ISqliteConnectionFactory connectionFactory, TimeProvider timeProvider)
    {
        _connectionFactory = connectionFactory;
        _clock = timeProvider;
    }

    public async Task<EntryId?> CreateGlobalAsync(
        SystemId systemId,
        CreateGlobalJournalEntryCommand command,
        CancellationToken cancellationToken = default)
    {
        var userKey = SqliteStorageKeys.Persist(systemId);
        var entryId = Guid.NewGuid();
        var nowMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO global_journals
                (user_id, id, title, content, color, pinned, locked, inserted_at, updated_at)
            VALUES
                (@user_id, @id, @title, NULL, NULL, 0, 0, @inserted_at, @updated_at)
            """,
            new
            {
                user_id = userKey,
                id = FormatEntryId(entryId),
                title = command.Title,
                inserted_at = nowMs,
                updated_at = nowMs,
            });

        return new EntryId(entryId);
    }

    public async Task<bool> ExistsGlobalAsync(
        SystemId systemId,
        EntryId entryId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        return await ExistsGlobalCoreAsync(connection, systemId, entryId, cancellationToken);
    }

    public async Task<bool> UpdateGlobalAsync(
        SystemId systemId,
        UpdateGlobalJournalEntryCommand command,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        if (!await ExistsGlobalCoreAsync(connection, systemId, command.EntryId, cancellationToken))
        {
            return false;
        }

        if (command.Title is null && command.Content is null && command.Color is null)
        {
            return true;
        }

        var updated = await connection.ExecuteAsync(
            """
            UPDATE global_journals SET
                title = COALESCE(@title, title),
                content = COALESCE(@content, content),
                color = COALESCE(@color, color),
                updated_at = @updated_at
            WHERE user_id = @user_id AND id = @id
            """,
            new
            {
                title = command.Title,
                content = command.Content,
                color = command.Color?.Value,
                updated_at = _clock.GetUtcNow().ToUnixTimeMilliseconds(),
                user_id = SqliteStorageKeys.Persist(systemId),
                id = FormatEntryId(command.EntryId.Value),
            });
        return updated > 0;
    }

    public async Task<bool> DeleteGlobalAsync(
        SystemId systemId,
        EntryId entryId,
        CancellationToken cancellationToken = default)
    {
        var userKey = SqliteStorageKeys.Persist(systemId);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        if (!await ExistsGlobalCoreAsync(connection, systemId, entryId, cancellationToken))
        {
            return false;
        }

        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await connection.ExecuteAsync(
                """
                DELETE FROM global_journal_alters
                WHERE user_id = @user_id AND global_journal_id = @id
                """,
                new { user_id = userKey, id = FormatEntryId(entryId.Value) },
                tx);

            var removed = await connection.ExecuteAsync(
                """
                DELETE FROM global_journals
                WHERE user_id = @user_id AND id = @id
                """,
                new { user_id = userKey, id = FormatEntryId(entryId.Value) },
                tx);

            await tx.CommitAsync(cancellationToken);
            return removed > 0;
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
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        if (!await ExistsGlobalCoreAsync(connection, systemId, entryId, cancellationToken))
        {
            return false;
        }

        var nowMs = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        await connection.ExecuteAsync(
            """
            INSERT INTO global_journal_alters
                (user_id, global_journal_id, alter_id, inserted_at, updated_at)
            VALUES
                (@user_id, @id, @alter_id, @inserted_at, @updated_at)
            ON CONFLICT(user_id, global_journal_id, alter_id) DO NOTHING
            """,
            new
            {
                user_id = SqliteStorageKeys.Persist(systemId),
                id = FormatEntryId(entryId.Value),
                alter_id = alterId.Value,
                inserted_at = nowMs,
                updated_at = nowMs,
            });
        return true;
    }

    public async Task<bool> DetachGlobalAlterAsync(
        SystemId systemId,
        EntryId entryId,
        AlterId alterId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var removed = await connection.ExecuteAsync(
            """
            DELETE FROM global_journal_alters
            WHERE user_id = @user_id
              AND global_journal_id = @id
              AND alter_id = @alter_id
            """,
            new
            {
                user_id = SqliteStorageKeys.Persist(systemId),
                id = FormatEntryId(entryId.Value),
                alter_id = alterId.Value,
            });
        return removed > 0;
    }

    public async Task<EntryId?> CreateAlterAsync(
        SystemId systemId,
        CreateAlterJournalEntryCommand command,
        CancellationToken cancellationToken = default)
    {
        var entryId = Guid.NewGuid();
        var atMs = command.CreatedAt.ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO alter_journals
                (user_id, id, alter_id, title, content, color, pinned, locked, inserted_at, updated_at)
            VALUES
                (@user_id, @id, @alter_id, @title, NULL, NULL, 0, 0, @inserted_at, @updated_at)
            """,
            new
            {
                user_id = SqliteStorageKeys.Persist(systemId),
                id = FormatEntryId(entryId),
                alter_id = command.AlterId.Value,
                title = command.Title,
                inserted_at = atMs,
                updated_at = atMs,
            });

        return new EntryId(entryId);
    }

    public async Task<AlterJournalRef?> GetAlterRefAsync(
        SystemId systemId,
        EntryId entryId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QueryFirstOrDefaultAsync<AlterJournalRefRow>(
            """
            SELECT id AS Id, alter_id AS AlterId
            FROM alter_journals
            WHERE user_id = @user_id AND id = @id
            LIMIT 1
            """,
            new
            {
                user_id = SqliteStorageKeys.Persist(systemId),
                id = FormatEntryId(entryId.Value),
            });
        if (row is null)
        {
            return null;
        }

        return new AlterJournalRef(
            new EntryId(ParseEntryId(row.Id)),
            new AlterId((short)row.AlterId));
    }

    public async Task<bool> UpdateAlterAsync(
        SystemId systemId,
        UpdateAlterJournalEntryCommand command,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        if (command.Title is null && command.Content is null && command.Color is null)
        {
            return await ExistsAlterAsync(connection, SqliteStorageKeys.Persist(systemId), command.EntryId, cancellationToken);
        }

        var updated = await connection.ExecuteAsync(
            """
            UPDATE alter_journals SET
                title = COALESCE(@title, title),
                content = COALESCE(@content, content),
                color = COALESCE(@color, color),
                updated_at = @updated_at
            WHERE user_id = @user_id AND id = @id
            """,
            new
            {
                title = command.Title,
                content = command.Content,
                color = command.Color?.Value,
                updated_at = command.UpdatedAt.ToUnixTimeMilliseconds(),
                user_id = SqliteStorageKeys.Persist(systemId),
                id = FormatEntryId(command.EntryId.Value),
            });
        return updated > 0;
    }

    public async Task<bool> DeleteAlterAsync(
        SystemId systemId,
        EntryId entryId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var removed = await connection.ExecuteAsync(
            """
            DELETE FROM alter_journals
            WHERE user_id = @user_id AND id = @id
            """,
            new
            {
                user_id = SqliteStorageKeys.Persist(systemId),
                id = FormatEntryId(entryId.Value),
            });
        return removed > 0;
    }

    public async Task<int> DeleteAllForAlterAsync(
        SystemId systemId,
        AlterId alterId,
        CancellationToken cancellationToken = default)
    {
        var userKey = SqliteStorageKeys.Persist(systemId);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var removed = await connection.ExecuteAsync(
                """
                DELETE FROM alter_journals
                WHERE user_id = @user_id AND alter_id = @alter_id
                """,
                new { user_id = userKey, alter_id = alterId.Value },
                tx);

            await connection.ExecuteAsync(
                """
                DELETE FROM global_journal_alters
                WHERE user_id = @user_id AND alter_id = @alter_id
                """,
                new { user_id = userKey, alter_id = alterId.Value },
                tx);

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
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<AlterJournalRow>(
            """
            SELECT id AS Id, user_id AS UserId, alter_id AS AlterId, title AS Title, content AS Content,
                   color AS Color, pinned AS Pinned, locked AS Locked, inserted_at AS InsertedAt, updated_at AS UpdatedAt
            FROM alter_journals
            WHERE user_id = @user_id AND alter_id = @alter_id
            ORDER BY inserted_at DESC
            """,
            new { user_id = SqliteStorageKeys.Persist(systemId), alter_id = alterId.Value });
        return rows.Select(MapAlterJournal).ToArray();
    }

    public async Task<AlterJournalReadModel?> GetAlterAsync(
        SystemId systemId,
        EntryId entryId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QueryFirstOrDefaultAsync<AlterJournalRow>(
            """
            SELECT id AS Id, user_id AS UserId, alter_id AS AlterId, title AS Title, content AS Content,
                   color AS Color, pinned AS Pinned, locked AS Locked, inserted_at AS InsertedAt, updated_at AS UpdatedAt
            FROM alter_journals
            WHERE user_id = @user_id AND id = @id
            LIMIT 1
            """,
            new
            {
                user_id = SqliteStorageKeys.Persist(systemId),
                id = FormatEntryId(entryId.Value),
            });
        return row is null ? null : MapAlterJournal(row);
    }

    public async Task<IReadOnlyList<JournalReadModel>> ListGlobalAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        var userKey = SqliteStorageKeys.Persist(systemId);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var drafts = (await connection.QueryAsync<GlobalJournalRow>(
            """
            SELECT id AS Id, user_id AS UserId, title AS Title, content AS Content, color AS Color,
                   pinned AS Pinned, locked AS Locked, inserted_at AS InsertedAt, updated_at AS UpdatedAt
            FROM global_journals
            WHERE user_id = @user_id
            """,
            new { user_id = userKey })).ToArray();

        var result = new List<JournalReadModel>(drafts.Length);
        foreach (var draft in drafts)
        {
            var alterIds = await LoadAlterIdsAsync(connection, userKey, draft.Id, cancellationToken);
            result.Add(MapGlobalJournal(draft) with { Alters = alterIds });
        }

        return result
            .OrderByDescending(e => e.Id.Value.ToString("N"), StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<JournalReadModel?> GetGlobalAsync(
        SystemId systemId,
        EntryId entryId,
        CancellationToken cancellationToken = default)
    {
        var userKey = SqliteStorageKeys.Persist(systemId);
        var idHex = FormatEntryId(entryId.Value);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QueryFirstOrDefaultAsync<GlobalJournalRow>(
            """
            SELECT id AS Id, user_id AS UserId, title AS Title, content AS Content, color AS Color,
                   pinned AS Pinned, locked AS Locked, inserted_at AS InsertedAt, updated_at AS UpdatedAt
            FROM global_journals
            WHERE user_id = @user_id AND id = @id
            LIMIT 1
            """,
            new { user_id = userKey, id = idHex });
        if (row is null)
        {
            return null;
        }

        var alterIds = await LoadAlterIdsAsync(connection, userKey, idHex, cancellationToken);
        return MapGlobalJournal(row) with { Alters = alterIds };
    }

    private async Task<bool> SetGlobalFlagAsync(
        SystemId systemId,
        EntryId entryId,
        string column,
        bool value,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        if (!await ExistsGlobalCoreAsync(connection, systemId, entryId, cancellationToken))
        {
            return false;
        }

        var sql = column == "pinned"
            ? """
              UPDATE global_journals
              SET pinned = @flag, updated_at = @updated_at
              WHERE user_id = @user_id AND id = @id
              """
            : """
              UPDATE global_journals
              SET locked = @flag, updated_at = @updated_at
              WHERE user_id = @user_id AND id = @id
              """;
        var rows = await connection.ExecuteAsync(
            sql,
            new
            {
                flag = value ? 1 : 0,
                updated_at = _clock.GetUtcNow().ToUnixTimeMilliseconds(),
                user_id = SqliteStorageKeys.Persist(systemId),
                id = FormatEntryId(entryId.Value),
            });
        return rows > 0;
    }

    private async Task<bool> SetAlterFlagAsync(
        SystemId systemId,
        EntryId entryId,
        string column,
        bool value,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var sql = column == "pinned"
            ? """
              UPDATE alter_journals
              SET pinned = @flag, updated_at = @updated_at
              WHERE user_id = @user_id AND id = @id
              """
            : """
              UPDATE alter_journals
              SET locked = @flag, updated_at = @updated_at
              WHERE user_id = @user_id AND id = @id
              """;
        var updated = await connection.ExecuteAsync(
            sql,
            new
            {
                flag = value ? 1 : 0,
                updated_at = _clock.GetUtcNow().ToUnixTimeMilliseconds(),
                user_id = SqliteStorageKeys.Persist(systemId),
                id = FormatEntryId(entryId.Value),
            });
        return updated > 0;
    }

    private static async Task<bool> ExistsGlobalCoreAsync(
        SqliteConnection connection,
        SystemId systemId,
        EntryId entryId,
        CancellationToken cancellationToken)
    {
        var hit = await connection.ExecuteScalarAsync(
            """
            SELECT 1 FROM global_journals
            WHERE user_id = @user_id AND id = @id
            LIMIT 1
            """,
            new
            {
                user_id = SqliteStorageKeys.Persist(systemId),
                id = FormatEntryId(entryId.Value),
            });
        return hit is not null and not DBNull;
    }

    private static async Task<bool> ExistsAlterAsync(
        SqliteConnection connection,
        string userKey,
        EntryId entryId,
        CancellationToken cancellationToken)
    {
        var hit = await connection.ExecuteScalarAsync(
            """
            SELECT 1 FROM alter_journals
            WHERE user_id = @user_id AND id = @id
            LIMIT 1
            """,
            new { user_id = userKey, id = FormatEntryId(entryId.Value) });
        return hit is not null and not DBNull;
    }

    private static async Task<IReadOnlyList<AlterId>> LoadAlterIdsAsync(
        SqliteConnection connection,
        string userKey,
        string entryIdHex,
        CancellationToken cancellationToken)
    {
        var alterIds = await connection.QueryAsync<long>(
            """
            SELECT alter_id
            FROM global_journal_alters
            WHERE user_id = @user_id AND global_journal_id = @id
            """,
            new { user_id = userKey, id = entryIdHex });
        return alterIds.Select(id => new AlterId((short)id)).ToArray();
    }

    private static JournalReadModel MapGlobalJournal(GlobalJournalRow row)
        => new(
            new EntryId(ParseEntryId(row.Id)),
            SqliteStorageKeys.ToWire(row.UserId),
            row.Title,
            row.Content,
            HexColor.FromNullable(row.Color),
            row.Locked != 0,
            row.Pinned != 0,
            FromUnixMs(row.InsertedAt),
            FromUnixMs(row.UpdatedAt),
            Array.Empty<AlterId>());

    private static AlterJournalReadModel MapAlterJournal(AlterJournalRow row)
        => new(
            new EntryId(ParseEntryId(row.Id)),
            SqliteStorageKeys.ToWire(row.UserId),
            new AlterId((short)row.AlterId),
            row.Title,
            row.Content,
            HexColor.FromNullable(row.Color),
            row.Locked != 0,
            row.Pinned != 0,
            FromUnixMs(row.InsertedAt),
            FromUnixMs(row.UpdatedAt));

    private static string FormatEntryId(Guid id) => id.ToString("N");

    private static Guid ParseEntryId(string hex) => Guid.ParseExact(hex, "N");

    private static DateTime FromUnixMs(long ms)
        => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    private sealed record AlterJournalRefRow(string Id, long AlterId);

    private sealed record GlobalJournalRow(
        string Id,
        string UserId,
        string Title,
        string? Content,
        string? Color,
        long Pinned,
        long Locked,
        long InsertedAt,
        long UpdatedAt);

    private sealed record AlterJournalRow(
        string Id,
        string UserId,
        long AlterId,
        string Title,
        string? Content,
        string? Color,
        long Pinned,
        long Locked,
        long InsertedAt,
        long UpdatedAt);
}
