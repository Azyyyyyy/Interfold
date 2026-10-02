using System.Diagnostics;
using Dapper;
using Interfold.Alters.Contracts.Models;
using Interfold.Alters.Domain.Abstractions.Repository;
using Interfold.Friendships.Domain.Abstractions.Repository;
using Interfold.Fronting.Contracts.Ids;
using Interfold.Fronting.Contracts.Models.Read;
using Interfold.Fronting.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Sqlite;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Domain.Observability;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Interfold.Infrastructure.Sqlite.Repository;

public sealed class SqliteFrontingRepository : IFrontingRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IAlterRepository _alters;
    private readonly ILogger<SqliteFrontingRepository> _logger;
    private readonly TimeProvider _timeProvider;

    public SqliteFrontingRepository(
        ISqliteConnectionFactory connectionFactory,
        IFriendshipRepository friendships,
        IAlterRepository alters,
        ILogger<SqliteFrontingRepository> logger,
        TimeProvider timeProvider)
    {
        _ = friendships;
        _connectionFactory = connectionFactory;
        _alters = alters;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<bool> IsFrontingAsync(
        SystemId systemId,
        AlterId alterId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var hit = await connection.ExecuteScalarAsync(
            """
            SELECT 1 FROM current_fronts
            WHERE user_id = @user_id AND alter_id = @alter_id
            LIMIT 1
            """,
            new { user_id = SqliteStorageKeys.Persist(systemId), alter_id = alterId.Value });
        return hit is not null and not DBNull;
    }

    public async Task<FrontId?> StartAsync(
        SystemId systemId,
        AlterId alterId,
        string? comment,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userKey = SqliteStorageKeys.Persist(systemId);
        var frontId = Guid.NewGuid();
        var frontIdText = frontId.ToString("N");
        var startedMs = startedAt.ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var already = await connection.ExecuteScalarAsync(
                """
                SELECT 1 FROM current_fronts
                WHERE user_id = @user_id AND alter_id = @alter_id
                LIMIT 1
                """,
                new { user_id = userKey, alter_id = alterId.Value },
                tx);
            if (already is not null and not DBNull)
            {
                await tx.RollbackAsync(cancellationToken);
                return null;
            }

            await connection.ExecuteAsync(
                """
                INSERT INTO current_fronts (user_id, alter_id, id, comment, time_start)
                VALUES (@user_id, @alter_id, @id, @comment, @time_start)
                """,
                new
                {
                    user_id = userKey,
                    alter_id = alterId.Value,
                    id = frontIdText,
                    comment,
                    time_start = startedMs,
                },
                tx);

            await connection.ExecuteAsync(
                """
                INSERT INTO fronts (user_id, id, alter_id, comment, time_start, time_end)
                VALUES (@user_id, @id, @alter_id, @comment, @time_start, NULL)
                """,
                new
                {
                    user_id = userKey,
                    id = frontIdText,
                    alter_id = alterId.Value,
                    comment,
                    time_start = startedMs,
                },
                tx);

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
        var userKey = SqliteStorageKeys.Persist(systemId);
        var endedMs = endedAt.ToUnixTimeMilliseconds();

        await using var work = await SqliteWork.OpenAsync(_connectionFactory, cancellationToken);
        var frontIdText = await work.Connection.QueryFirstOrDefaultAsync<string>(
            """
            SELECT id FROM current_fronts
            WHERE user_id = @user_id AND alter_id = @alter_id
            LIMIT 1
            """,
            new { user_id = userKey, alter_id = alterId.Value },
            work.Transaction);

        if (frontIdText is null)
            return false;

        await work.Connection.ExecuteAsync(
            """
            DELETE FROM current_fronts
            WHERE user_id = @user_id AND alter_id = @alter_id
            """,
            new { user_id = userKey, alter_id = alterId.Value },
            work.Transaction);

        await work.Connection.ExecuteAsync(
            """
            UPDATE front_primary
            SET alter_id = NULL
            WHERE user_id = @user_id AND alter_id = @alter_id
            """,
            new { user_id = userKey, alter_id = alterId.Value },
            work.Transaction);

        await work.Connection.ExecuteAsync(
            """
            UPDATE fronts
            SET time_end = @time_end
            WHERE user_id = @user_id AND id = @id AND time_end IS NULL
            """,
            new { time_end = endedMs, user_id = userKey, id = frontIdText },
            work.Transaction);

        await work.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SetPrimaryAsync(
        SystemId systemId,
        AlterId? alterId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userKey = SqliteStorageKeys.Persist(systemId);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        if (alterId is { } value)
        {
            var hit = await connection.ExecuteScalarAsync(
                """
                SELECT 1 FROM current_fronts
                WHERE user_id = @user_id AND alter_id = @alter_id
                LIMIT 1
                """,
                new { user_id = userKey, alter_id = value.Value });
            if (hit is null or DBNull)
            {
                return false;
            }
        }

        var rows = await connection.ExecuteAsync(
            """
            INSERT INTO front_primary (user_id, alter_id)
            VALUES (@user_id, @alter_id)
            ON CONFLICT(user_id) DO UPDATE SET alter_id = excluded.alter_id
            """,
            new { user_id = userKey, alter_id = alterId?.Value });
        return rows > 0;
    }

    public async Task<IReadOnlyList<FrontActiveReadModel>> ListActiveAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userKey = SqliteStorageKeys.Persist(systemId);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        AlterId? primaryId = null;
        var primaryRaw = await connection.QueryFirstOrDefaultAsync<long?>(
            """
            SELECT alter_id FROM front_primary
            WHERE user_id = @user_id
            LIMIT 1
            """,
            new { user_id = userKey });
        if (primaryRaw is not null)
        {
            primaryId = new AlterId((short)primaryRaw.Value);
        }

        var rows = (await connection.QueryAsync<CurrentFrontRow>(
            """
            SELECT id AS Id, alter_id AS AlterId, comment AS Comment, time_start AS TimeStart
            FROM current_fronts
            WHERE user_id = @user_id
            ORDER BY time_start DESC
            """,
            new { user_id = userKey })).ToArray();

        var results = new List<FrontActiveReadModel>(rows.Length);
        foreach (var row in rows)
        {
            var alterId = new AlterId((short)row.AlterId);
            var frontId = FrontId.Parse(row.Id, provider: null);
            var startedAt = DateTimeOffset.FromUnixTimeMilliseconds(row.TimeStart);

            var alterModel = await _alters.GetAsync(systemId, alterId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Alter {alterId.Value} is missing but referenced by an active front.");
            var bareAlter = new BareAlter(
                alterId,
                alterModel.Name,
                alterModel.AvatarUrl,
                alterModel.AvatarSource,
                alterModel.Color,
                alterModel.Pronouns,
                alterModel.Description,
                alterModel.Fields);

            results.Add(new FrontActiveReadModel(
                bareAlter,
                new FrontHistoryReadModel(frontId, alterId, row.Comment, startedAt, null, systemId),
                primaryId == alterId));
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

        var all = await ListActiveAsync(systemId, cancellationToken);
        if (all.Count == 0)
        {
            GuardedInstrumentation.RecordList(
                _logger, "fronting", nameof(ListActiveGuardedAsync), viewerSystemId, ownerId,
                totalCount: 0, visibleCount: 0, sw.Elapsed.TotalMilliseconds);
            return all;
        }

        var guardedAlters = await _alters.ListGuardedAsync(systemId, viewerSystemId, cancellationToken);
        var visibleIds = guardedAlters.Select(a => a.Id).ToHashSet();
        var visible = all.Where(front => visibleIds.Contains(front.Alter.Id)).ToArray();
        GuardedInstrumentation.RecordList(
            _logger, "fronting", nameof(ListActiveGuardedAsync), viewerSystemId, ownerId,
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
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<FrontHistoryRow>(
            """
            SELECT id AS Id, alter_id AS AlterId, comment AS Comment, time_start AS TimeStart, time_end AS TimeEnd
            FROM fronts
            WHERE user_id = @user_id
              AND time_start >= @start
              AND time_start <= @end
              AND time_end IS NOT NULL
            ORDER BY time_start DESC
            """,
            new
            {
                user_id = SqliteStorageKeys.Persist(systemId),
                start = startInclusive.ToUnixTimeMilliseconds(),
                end = endInclusive.ToUnixTimeMilliseconds(),
            });
        return rows.Select(r => MapHistoryRow(r, systemId)).ToArray();
    }

    public async Task<IReadOnlyList<FrontHistoryReadModel>> ListAllAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<FrontHistoryRow>(
            """
            SELECT id AS Id, alter_id AS AlterId, comment AS Comment, time_start AS TimeStart, time_end AS TimeEnd
            FROM fronts
            WHERE user_id = @user_id
            ORDER BY time_start DESC
            """,
            new { user_id = SqliteStorageKeys.Persist(systemId) });
        return rows.Select(r => MapHistoryRow(r, systemId)).ToArray();
    }

    public async Task<FrontActiveReadModel?> GetActiveByFrontIdAsync(
        SystemId systemId,
        FrontId frontId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userKey = SqliteStorageKeys.Persist(systemId);
        var frontIdText = frontId.Value.ToString("N");

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        AlterId? primaryId = null;
        var primaryRaw = await connection.QueryFirstOrDefaultAsync<long?>(
            """
            SELECT alter_id FROM front_primary
            WHERE user_id = @user_id
            LIMIT 1
            """,
            new { user_id = userKey });
        if (primaryRaw is not null)
        {
            primaryId = new AlterId((short)primaryRaw.Value);
        }

        var row = await connection.QueryFirstOrDefaultAsync<ActiveFrontByIdRow>(
            """
            SELECT alter_id AS AlterId, comment AS Comment, time_start AS TimeStart
            FROM current_fronts
            WHERE user_id = @user_id AND id = @id
            LIMIT 1
            """,
            new { user_id = userKey, id = frontIdText });
        if (row is null)
        {
            return null;
        }

        var alterId = new AlterId((short)row.AlterId);
        var startedAt = DateTimeOffset.FromUnixTimeMilliseconds(row.TimeStart);
        var alterModel = await _alters.GetAsync(systemId, alterId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Alter {alterId.Value} is missing but referenced by an active front.");
        var bareAlter = new BareAlter(
            alterId,
            alterModel.Name,
            alterModel.AvatarUrl,
            alterModel.AvatarSource,
            alterModel.Color,
            alterModel.Pronouns,
            alterModel.Description,
            alterModel.Fields);

        return new FrontActiveReadModel(
            bareAlter,
            new FrontHistoryReadModel(frontId, alterId, row.Comment, startedAt, null, systemId),
            primaryId == alterId);
    }

    public async Task<FrontHistoryReadModel?> GetHistoryEntryByFrontIdAsync(
        SystemId systemId,
        FrontId frontId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QueryFirstOrDefaultAsync<FrontHistoryRow>(
            """
            SELECT id AS Id, alter_id AS AlterId, comment AS Comment, time_start AS TimeStart, time_end AS TimeEnd
            FROM fronts
            WHERE user_id = @user_id AND id = @id
            LIMIT 1
            """,
            new
            {
                user_id = SqliteStorageKeys.Persist(systemId),
                id = frontId.Value.ToString("N"),
            });
        return row is null ? null : MapHistoryRow(row, systemId);
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

        return await EndAsync(systemId, found.Front.AlterId, _timeProvider.GetUtcNow(), cancellationToken);
    }

    public async Task<FrontAlterRemoval> DeleteAllForAlterAsync(
        SystemId systemId,
        AlterId alterId,
        CancellationToken cancellationToken = default)
    {
        var userId = SqliteStorageKeys.Persist(systemId);
        await using var work = await SqliteWork.OpenAsync(_connectionFactory, cancellationToken);

        var frontIds = (await work.Connection.QueryAsync<string>(
            """
            SELECT id FROM fronts
            WHERE user_id = @user_id AND alter_id = @alter_id
            """,
            new { user_id = userId, alter_id = alterId.Value },
            work.Transaction)).Select(id => FrontId.Parse(id, null)).ToArray();

        var hadActiveFront = await work.Connection.ExecuteScalarAsync<long?>(
            """
            SELECT 1 FROM current_fronts
            WHERE user_id = @user_id AND alter_id = @alter_id
            LIMIT 1
            """,
            new { user_id = userId, alter_id = alterId.Value },
            work.Transaction) is not null;

        var primaryCleared = await work.Connection.ExecuteScalarAsync<long?>(
            """
            SELECT 1 FROM front_primary
            WHERE user_id = @user_id AND alter_id = @alter_id
            LIMIT 1
            """,
            new { user_id = userId, alter_id = alterId.Value },
            work.Transaction) is not null;

        // ListActiveAsync refuses an active front whose alter is gone, which fails socket join.
        await work.Connection.ExecuteAsync(
            """
            DELETE FROM current_fronts
            WHERE user_id = @user_id AND alter_id = @alter_id
            """,
            new { user_id = userId, alter_id = alterId.Value },
            work.Transaction);

        await work.Connection.ExecuteAsync(
            """
            DELETE FROM fronts
            WHERE user_id = @user_id AND alter_id = @alter_id
            """,
            new { user_id = userId, alter_id = alterId.Value },
            work.Transaction);

        await work.Connection.ExecuteAsync(
            """
            UPDATE front_primary
            SET alter_id = NULL
            WHERE user_id = @user_id AND alter_id = @alter_id
            """,
            new { user_id = userId, alter_id = alterId.Value },
            work.Transaction);

        await work.CommitAsync(cancellationToken);
        return new FrontAlterRemoval(frontIds, hadActiveFront, primaryCleared);
    }

    public async Task<bool> DeleteFrontByIdAsync(
        SystemId systemId,
        FrontId frontId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userKey = SqliteStorageKeys.Persist(systemId);
        var frontIdText = frontId.Value.ToString("N");

        await using var work = await SqliteWork.OpenAsync(_connectionFactory, cancellationToken);
        var alterRaw = await work.Connection.QueryFirstOrDefaultAsync<long?>(
            """
            SELECT alter_id FROM fronts
            WHERE user_id = @user_id AND id = @id
            LIMIT 1
            """,
            new { user_id = userKey, id = frontIdText },
            work.Transaction);
        if (alterRaw is null)
            return false;

        await work.Connection.ExecuteAsync(
            "DELETE FROM fronts WHERE user_id = @user_id AND id = @id",
            new { user_id = userKey, id = frontIdText },
            work.Transaction);

        await work.Connection.ExecuteAsync(
            """
            DELETE FROM current_fronts
            WHERE user_id = @user_id AND id = @id
            """,
            new { user_id = userKey, id = frontIdText },
            work.Transaction);

        await work.Connection.ExecuteAsync(
            """
            UPDATE front_primary
            SET alter_id = NULL
            WHERE user_id = @user_id AND alter_id = @alter_id
            """,
            new { user_id = userKey, alter_id = (short)alterRaw.Value },
            work.Transaction);

        await work.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> UpdateCommentByFrontIdAsync(
        SystemId systemId,
        FrontId frontId,
        string comment,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userKey = SqliteStorageKeys.Persist(systemId);
        var frontIdText = frontId.Value.ToString("N");

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var activeUpdated = await connection.ExecuteAsync(
                """
                UPDATE current_fronts
                SET comment = @comment
                WHERE user_id = @user_id AND id = @id
                """,
                new { comment, user_id = userKey, id = frontIdText },
                tx);

            if (activeUpdated == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return false;
            }

            await connection.ExecuteAsync(
                """
                UPDATE fronts
                SET comment = @comment
                WHERE user_id = @user_id AND id = @id AND time_end IS NULL
                """,
                new { comment, user_id = userKey, id = frontIdText },
                tx);

            await tx.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static FrontHistoryReadModel MapHistoryRow(FrontHistoryRow row, SystemId systemId)
    {
        var frontId = FrontId.Parse(row.Id, provider: null);
        var alterId = new AlterId((short)row.AlterId);
        var startedAt = DateTimeOffset.FromUnixTimeMilliseconds(row.TimeStart);
        DateTimeOffset? endedAt = row.TimeEnd is null
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(row.TimeEnd.Value);
        return new FrontHistoryReadModel(frontId, alterId, row.Comment, startedAt, endedAt, systemId);
    }

    private sealed record CurrentFrontRow(string Id, long AlterId, string? Comment, long TimeStart);

    private sealed record ActiveFrontByIdRow(long AlterId, string? Comment, long TimeStart);

    private sealed record FrontHistoryRow(
        string Id,
        long AlterId,
        string? Comment,
        long TimeStart,
        long? TimeEnd);
}
