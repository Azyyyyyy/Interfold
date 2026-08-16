using System.Diagnostics;
using Interfold.Alters.Contracts.Models;
using Interfold.Alters.Domain.Abstractions.Repository;
using Interfold.Friendships.Domain.Abstractions.Repository;
using Interfold.Fronting.Contracts.Ids;
using Interfold.Fronting.Contracts.Models.Read;
using Interfold.Fronting.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Observability;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Interfold.Infrastructure.Sqlite.Repository;

public sealed class SqliteFrontingRepository(
    ISqliteConnectionFactory connectionFactory,
    IRegionContext regionContext,
    IFriendshipRepository friendships,
    IAlterRepository alters,
    ILogger<SqliteFrontingRepository> logger) : IFrontingRepository
{
    public async Task<bool> IsFrontingAsync(
        SystemId systemId,
        AlterId alterId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM current_fronts
            WHERE user_id = $user_id AND alter_id = $alter_id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$user_id", userId);
        command.Parameters.AddWithValue("$alter_id", alterId.Value);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<FrontId?> StartAsync(
        SystemId systemId,
        AlterId alterId,
        string? comment,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        var frontId = Guid.NewGuid();
        var frontIdText = frontId.ToString("N");
        var startedMs = startedAt.ToUnixTimeMilliseconds();

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var exists = connection.CreateCommand())
            {
                exists.Transaction = tx;
                exists.CommandText = """
                    SELECT 1 FROM current_fronts
                    WHERE user_id = $user_id AND alter_id = $alter_id
                    LIMIT 1
                    """;
                exists.Parameters.AddWithValue("$user_id", userId);
                exists.Parameters.AddWithValue("$alter_id", alterId.Value);
                if (await exists.ExecuteScalarAsync(cancellationToken) is not null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return null;
                }
            }

            await using (var insertActive = connection.CreateCommand())
            {
                insertActive.Transaction = tx;
                insertActive.CommandText = """
                    INSERT INTO current_fronts (user_id, alter_id, id, comment, time_start)
                    VALUES ($user_id, $alter_id, $id, $comment, $time_start)
                    """;
                insertActive.Parameters.AddWithValue("$user_id", userId);
                insertActive.Parameters.AddWithValue("$alter_id", alterId.Value);
                insertActive.Parameters.AddWithValue("$id", frontIdText);
                insertActive.Parameters.AddWithValue("$comment", (object?)comment ?? DBNull.Value);
                insertActive.Parameters.AddWithValue("$time_start", startedMs);
                await insertActive.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var insertHistory = connection.CreateCommand())
            {
                insertHistory.Transaction = tx;
                insertHistory.CommandText = """
                    INSERT INTO fronts (user_id, id, alter_id, comment, time_start, time_end)
                    VALUES ($user_id, $id, $alter_id, $comment, $time_start, NULL)
                    """;
                insertHistory.Parameters.AddWithValue("$user_id", userId);
                insertHistory.Parameters.AddWithValue("$id", frontIdText);
                insertHistory.Parameters.AddWithValue("$alter_id", alterId.Value);
                insertHistory.Parameters.AddWithValue("$comment", (object?)comment ?? DBNull.Value);
                insertHistory.Parameters.AddWithValue("$time_start", startedMs);
                await insertHistory.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            return new FrontId(frontId);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> EndAsync(
        SystemId systemId,
        AlterId alterId,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        var endedMs = endedAt.ToUnixTimeMilliseconds();

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            string? frontIdText;
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = tx;
                select.CommandText = """
                    SELECT id FROM current_fronts
                    WHERE user_id = $user_id AND alter_id = $alter_id
                    LIMIT 1
                    """;
                select.Parameters.AddWithValue("$user_id", userId);
                select.Parameters.AddWithValue("$alter_id", alterId.Value);
                frontIdText = await select.ExecuteScalarAsync(cancellationToken) as string;
            }

            if (frontIdText is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return false;
            }

            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = """
                    DELETE FROM current_fronts
                    WHERE user_id = $user_id AND alter_id = $alter_id
                    """;
                delete.Parameters.AddWithValue("$user_id", userId);
                delete.Parameters.AddWithValue("$alter_id", alterId.Value);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var clearPrimary = connection.CreateCommand())
            {
                clearPrimary.Transaction = tx;
                clearPrimary.CommandText = """
                    UPDATE front_primary
                    SET alter_id = NULL
                    WHERE user_id = $user_id AND alter_id = $alter_id
                    """;
                clearPrimary.Parameters.AddWithValue("$user_id", userId);
                clearPrimary.Parameters.AddWithValue("$alter_id", alterId.Value);
                await clearPrimary.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var closeHistory = connection.CreateCommand())
            {
                closeHistory.Transaction = tx;
                closeHistory.CommandText = """
                    UPDATE fronts
                    SET time_end = $time_end
                    WHERE user_id = $user_id AND id = $id AND time_end IS NULL
                    """;
                closeHistory.Parameters.AddWithValue("$time_end", endedMs);
                closeHistory.Parameters.AddWithValue("$user_id", userId);
                closeHistory.Parameters.AddWithValue("$id", frontIdText);
                await closeHistory.ExecuteNonQueryAsync(cancellationToken);
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

    public async Task<bool> SetPrimaryAsync(
        SystemId systemId,
        AlterId? alterId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        if (alterId is { } value)
        {
            await using var exists = connection.CreateCommand();
            exists.CommandText = """
                SELECT 1 FROM current_fronts
                WHERE user_id = $user_id AND alter_id = $alter_id
                LIMIT 1
                """;
            exists.Parameters.AddWithValue("$user_id", userId);
            exists.Parameters.AddWithValue("$alter_id", value.Value);
            if (await exists.ExecuteScalarAsync(cancellationToken) is null)
            {
                return false;
            }
        }

        await using var upsert = connection.CreateCommand();
        upsert.CommandText = """
            INSERT INTO front_primary (user_id, alter_id)
            VALUES ($user_id, $alter_id)
            ON CONFLICT(user_id) DO UPDATE SET alter_id = excluded.alter_id
            """;
        upsert.Parameters.AddWithValue("$user_id", userId);
        upsert.Parameters.AddWithValue("$alter_id", alterId is { } a ? a.Value : DBNull.Value);
        await upsert.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<FrontActiveReadModel>> ListActiveAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        AlterId? primaryId = null;
        await using (var primaryCmd = connection.CreateCommand())
        {
            primaryCmd.CommandText = """
                SELECT alter_id FROM front_primary
                WHERE user_id = $user_id
                LIMIT 1
                """;
            primaryCmd.Parameters.AddWithValue("$user_id", userId);
            await using var primaryReader = await primaryCmd.ExecuteReaderAsync(cancellationToken);
            if (await primaryReader.ReadAsync(cancellationToken) && !primaryReader.IsDBNull(0))
            {
                primaryId = new AlterId((short)primaryReader.GetInt64(0));
            }
        }

        var rows = new List<(FrontId FrontId, AlterId AlterId, string? Comment, DateTimeOffset StartedAt)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, alter_id, comment, time_start
                FROM current_fronts
                WHERE user_id = $user_id
                ORDER BY time_start DESC
                """;
            command.Parameters.AddWithValue("$user_id", userId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    FrontId.Parse(reader.GetString(0), provider: null),
                    new AlterId((short)reader.GetInt64(1)),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3))));
            }
        }

        var results = new List<FrontActiveReadModel>(rows.Count);
        foreach (var row in rows)
        {
            var alterModel = await alters.GetAsync(systemId, row.AlterId, cancellationToken);
            var bareAlter = alterModel is not null
                ? new BareAlter(
                    row.AlterId,
                    alterModel.Name,
                    alterModel.AvatarUrl,
                    alterModel.AvatarSource,
                    alterModel.Color,
                    alterModel.Pronouns,
                    alterModel.Description,
                    alterModel.Fields)
                : BareAlter.CreatePlaceholder(row.AlterId);

            results.Add(new FrontActiveReadModel(
                bareAlter,
                new FrontHistoryReadModel(row.FrontId, row.AlterId, row.Comment, row.StartedAt, null, systemId),
                primaryId == row.AlterId));
        }

        return results;
    }

    public async Task<IReadOnlyList<FrontActiveReadModel>> ListActiveGuardedAsync(
        SystemId systemId,
        SystemId? viewerSystemId,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var ownerId = SqliteStorageKeys.Normalize(systemId).Value;
        var friendshipLevel = await SqliteStorageKeys.ResolveFriendshipLevelAsync(
            systemId, viewerSystemId, friendships, cancellationToken);
        if (!VisibilityLevel.Public.CanBeViewedBy(friendshipLevel))
        {
            GuardedInstrumentation.RecordList(
                logger, "fronting", nameof(ListActiveGuardedAsync), viewerSystemId, ownerId,
                totalCount: 0, visibleCount: 0, sw.Elapsed.TotalMilliseconds);
            return Array.Empty<FrontActiveReadModel>();
        }

        var all = await ListActiveAsync(systemId, cancellationToken);
        if (all.Count == 0)
        {
            GuardedInstrumentation.RecordList(
                logger, "fronting", nameof(ListActiveGuardedAsync), viewerSystemId, ownerId,
                totalCount: 0, visibleCount: 0, sw.Elapsed.TotalMilliseconds);
            return all;
        }

        var guardedAlters = await alters.ListGuardedAsync(systemId, viewerSystemId, cancellationToken);
        var visibleIds = guardedAlters.Select(a => a.Id).ToHashSet();
        var visible = all.Where(front => visibleIds.Contains(front.Alter.Id)).ToArray();
        GuardedInstrumentation.RecordList(
            logger, "fronting", nameof(ListActiveGuardedAsync), viewerSystemId, ownerId,
            all.Count, visible.Length, sw.Elapsed.TotalMilliseconds);
        return visible;
    }

    public async Task<IReadOnlyList<FrontHistoryReadModel>> ListHistoryBetweenAsync(
        SystemId systemId,
        DateTimeOffset startInclusive,
        DateTimeOffset endInclusive,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, alter_id, comment, time_start, time_end
            FROM fronts
            WHERE user_id = $user_id
              AND time_start >= $start
              AND time_start <= $end
            ORDER BY time_start DESC
            """;
        command.Parameters.AddWithValue("$user_id", userId);
        command.Parameters.AddWithValue("$start", startInclusive.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$end", endInclusive.ToUnixTimeMilliseconds());

        return await ReadHistoryAsync(command, systemId, cancellationToken);
    }

    public async Task<IReadOnlyList<FrontHistoryReadModel>> ListAllAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, alter_id, comment, time_start, time_end
            FROM fronts
            WHERE user_id = $user_id
            ORDER BY time_start DESC
            """;
        command.Parameters.AddWithValue("$user_id", userId);

        return await ReadHistoryAsync(command, systemId, cancellationToken);
    }

    public async Task<FrontActiveReadModel?> GetActiveByFrontIdAsync(
        SystemId systemId,
        FrontId frontId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        var frontIdText = frontId.Value.ToString("N");

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        AlterId? primaryId = null;
        await using (var primaryCmd = connection.CreateCommand())
        {
            primaryCmd.CommandText = """
                SELECT alter_id FROM front_primary
                WHERE user_id = $user_id
                LIMIT 1
                """;
            primaryCmd.Parameters.AddWithValue("$user_id", userId);
            await using var primaryReader = await primaryCmd.ExecuteReaderAsync(cancellationToken);
            if (await primaryReader.ReadAsync(cancellationToken) && !primaryReader.IsDBNull(0))
            {
                primaryId = new AlterId((short)primaryReader.GetInt64(0));
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT alter_id, comment, time_start
            FROM current_fronts
            WHERE user_id = $user_id AND id = $id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$user_id", userId);
        command.Parameters.AddWithValue("$id", frontIdText);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var alterId = new AlterId((short)reader.GetInt64(0));
        var comment = reader.IsDBNull(1) ? null : reader.GetString(1);
        var startedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2));

        return new FrontActiveReadModel(
            BareAlter.CreatePlaceholder(alterId),
            new FrontHistoryReadModel(frontId, alterId, comment, startedAt, null, systemId),
            primaryId == alterId);
    }

    public async Task<FrontHistoryReadModel?> GetHistoryEntryByFrontIdAsync(
        SystemId systemId,
        FrontId frontId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, alter_id, comment, time_start, time_end
            FROM fronts
            WHERE user_id = $user_id AND id = $id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$user_id", userId);
        command.Parameters.AddWithValue("$id", frontId.Value.ToString("N"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadHistoryRow(reader, systemId);
    }

    public async Task<bool> EndByFrontIdAsync(
        SystemId systemId,
        FrontId frontId,
        CancellationToken cancellationToken = default)
    {
        var found = await GetActiveByFrontIdAsync(systemId, frontId, cancellationToken);
        if (found is null)
        {
            return false;
        }

        return await EndAsync(systemId, found.Front.AlterId, DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task<bool> DeleteFrontByIdAsync(
        SystemId systemId,
        FrontId frontId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        var frontIdText = frontId.Value.ToString("N");

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            short? alterId = null;
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = tx;
                select.CommandText = """
                    SELECT alter_id FROM fronts
                    WHERE user_id = $user_id AND id = $id
                    LIMIT 1
                    """;
                select.Parameters.AddWithValue("$user_id", userId);
                select.Parameters.AddWithValue("$id", frontIdText);
                var scalar = await select.ExecuteScalarAsync(cancellationToken);
                if (scalar is null || scalar is DBNull)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return false;
                }

                alterId = Convert.ToInt16(scalar);
            }

            await using (var deleteHistory = connection.CreateCommand())
            {
                deleteHistory.Transaction = tx;
                deleteHistory.CommandText = "DELETE FROM fronts WHERE user_id = $user_id AND id = $id";
                deleteHistory.Parameters.AddWithValue("$user_id", userId);
                deleteHistory.Parameters.AddWithValue("$id", frontIdText);
                await deleteHistory.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var deleteActive = connection.CreateCommand())
            {
                deleteActive.Transaction = tx;
                deleteActive.CommandText = """
                    DELETE FROM current_fronts
                    WHERE user_id = $user_id AND id = $id
                    """;
                deleteActive.Parameters.AddWithValue("$user_id", userId);
                deleteActive.Parameters.AddWithValue("$id", frontIdText);
                await deleteActive.ExecuteNonQueryAsync(cancellationToken);
            }

            if (alterId is { } a)
            {
                await using var clearPrimary = connection.CreateCommand();
                clearPrimary.Transaction = tx;
                clearPrimary.CommandText = """
                    UPDATE front_primary
                    SET alter_id = NULL
                    WHERE user_id = $user_id AND alter_id = $alter_id
                    """;
                clearPrimary.Parameters.AddWithValue("$user_id", userId);
                clearPrimary.Parameters.AddWithValue("$alter_id", a);
                await clearPrimary.ExecuteNonQueryAsync(cancellationToken);
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

    public async Task<bool> UpdateCommentByFrontIdAsync(
        SystemId systemId,
        FrontId frontId,
        string comment,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userId = SqliteStorageKeys.ForSystem(regionContext, systemId).Value;
        var frontIdText = frontId.Value.ToString("N");

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            int activeUpdated;
            await using (var updateActive = connection.CreateCommand())
            {
                updateActive.Transaction = tx;
                updateActive.CommandText = """
                    UPDATE current_fronts
                    SET comment = $comment
                    WHERE user_id = $user_id AND id = $id
                    """;
                updateActive.Parameters.AddWithValue("$comment", comment);
                updateActive.Parameters.AddWithValue("$user_id", userId);
                updateActive.Parameters.AddWithValue("$id", frontIdText);
                activeUpdated = await updateActive.ExecuteNonQueryAsync(cancellationToken);
            }

            if (activeUpdated == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return false;
            }

            await using (var updateHistory = connection.CreateCommand())
            {
                updateHistory.Transaction = tx;
                updateHistory.CommandText = """
                    UPDATE fronts
                    SET comment = $comment
                    WHERE user_id = $user_id AND id = $id AND time_end IS NULL
                    """;
                updateHistory.Parameters.AddWithValue("$comment", comment);
                updateHistory.Parameters.AddWithValue("$user_id", userId);
                updateHistory.Parameters.AddWithValue("$id", frontIdText);
                await updateHistory.ExecuteNonQueryAsync(cancellationToken);
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

    private static async Task<IReadOnlyList<FrontHistoryReadModel>> ReadHistoryAsync(
        SqliteCommand command,
        SystemId systemId,
        CancellationToken cancellationToken)
    {
        var results = new List<FrontHistoryReadModel>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadHistoryRow(reader, systemId));
        }

        return results;
    }

    private static FrontHistoryReadModel ReadHistoryRow(SqliteDataReader reader, SystemId systemId)
    {
        var frontId = FrontId.Parse(reader.GetString(0), provider: null);
        var alterId = new AlterId((short)reader.GetInt64(1));
        var comment = reader.IsDBNull(2) ? null : reader.GetString(2);
        var startedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3));
        DateTimeOffset? endedAt = reader.IsDBNull(4)
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4));
        return new FrontHistoryReadModel(frontId, alterId, comment, startedAt, endedAt, systemId);
    }
}
