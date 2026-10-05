using Dapper;
using Interfold.Friendships.Contracts.Ids;
using Interfold.Friendships.Contracts.Models.Read;
using Interfold.Friendships.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Sqlite;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Microsoft.Data.Sqlite;
using System.Data.Common;

namespace Interfold.Infrastructure.Sqlite.Repository;

public sealed class SqliteFriendshipRepository : IFriendshipRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IAccountRepository _accounts;

    public SqliteFriendshipRepository(
        ISqliteConnectionFactory connectionFactory,
        TimeProvider timeProvider,
        IAccountRepository accounts)
    {
        _connectionFactory = connectionFactory;
        _timeProvider = timeProvider;
        _accounts = accounts;
    }

    public async Task<SystemId?> ResolveUserIdAsync(FriendLookup lookup, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (lookup.Kind == FriendLookupKind.Id)
            return new SystemId(lookup.Value);

        if (lookup.Kind == FriendLookupKind.Username)
            return await _accounts.TryFindSystemIdByUsernameAsync(new Username(lookup.Value), cancellationToken);

        throw new ArgumentOutOfRangeException(nameof(lookup), lookup.Kind,
            $"Unhandled FriendLookupKind '{lookup.Kind}' in ResolveUserIdAsync.");
    }

    public async Task<FriendshipLevel?> GetFriendshipLevelAsync(
        SystemId systemId,
        SystemId? viewerSystemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (viewerSystemId is null || string.IsNullOrWhiteSpace(viewerSystemId.Value.Value))
        {
            return null;
        }

        var viewerKey = viewerSystemId.Value;
        if (systemId == viewerKey)
        {
            return FriendshipLevel.TrustedFriend;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var raw = await connection.ExecuteScalarAsync(
            """
            SELECT level
            FROM friendships
            WHERE user_id = @user_id AND friend_id = @friend_id
            LIMIT 1
            """,
            new { user_id = systemId.Value, friend_id = viewerKey.Value });
        if (raw is null or DBNull)
        {
            return null;
        }

        return ((short)(long)raw).FromCode<FriendshipLevel>();
    }

    public async Task<IReadOnlyList<FriendshipReadModel>> ListFriendshipsAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<FriendshipRow>(
            """
            SELECT friend_id AS FriendId, level AS Level, since AS Since
            FROM friendships
            WHERE user_id = @user_id
            ORDER BY since DESC
            """,
            new { user_id = systemId.Value });

        return rows.Select(MapFriendship).ToArray();
    }

    public async Task<FriendshipReadModel?> GetFriendshipAsync(
        SystemId systemId,
        SystemId friendSystemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QueryFirstOrDefaultAsync<FriendshipRow>(
            """
            SELECT friend_id AS FriendId, level AS Level, since AS Since
            FROM friendships
            WHERE user_id = @user_id AND friend_id = @friend_id
            LIMIT 1
            """,
            new
            {
                user_id = systemId.Value,
                friend_id = friendSystemId.Value,
            });
        return row is null ? null : MapFriendship(row);
    }

    public async Task<bool> RemoveFriendshipAsync(
        SystemId systemId,
        SystemId friendSystemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var right = friendSystemId;

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var removed = await DeleteFriendshipEdgeAsync(connection, tx, systemId, right, cancellationToken);
            if (!removed)
            {
                await tx.RollbackAsync(cancellationToken);
                return false;
            }

            await DeleteFriendshipEdgeAsync(connection, tx, right, systemId, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> SetTrustedAsync(
        SystemId systemId,
        SystemId friendSystemId,
        bool trusted,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var level = (short)(trusted ? FriendshipLevel.TrustedFriend : FriendshipLevel.Friend);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(
            """
            UPDATE friendships
            SET level = @level
            WHERE user_id = @user_id AND friend_id = @friend_id
            """,
            new
            {
                level,
                user_id = systemId.Value,
                friend_id = friendSystemId.Value,
            });
        return affected > 0;
    }

    public async Task<FriendRequestIndexReadModel> GetFriendRequestsAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var outgoingRows = await connection.QueryAsync<FriendRequestRow>(
            """
            SELECT to_user_id AS OtherUserId, date_sent AS DateSent
            FROM friend_requests
            WHERE from_user_id = @user_id
            ORDER BY date_sent DESC
            """,
            new { user_id = systemId.Value });

        var incomingRows = await connection.QueryAsync<FriendRequestRow>(
            """
            SELECT from_user_id AS OtherUserId, date_sent AS DateSent
            FROM friend_requests
            WHERE to_user_id = @user_id
            ORDER BY date_sent DESC
            """,
            new { user_id = systemId.Value });

        return new FriendRequestIndexReadModel(
            incomingRows.Select(MapRequest).ToArray(),
            outgoingRows.Select(MapRequest).ToArray());
    }

    public async Task<SendFriendRequestOutcome> SendRequestAsync(
        SystemId systemId,
        SystemId targetSystemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var from = systemId;
        var to = targetSystemId;

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (await FriendshipExistsAsync(connection, tx, from, to, cancellationToken))
            {
                await tx.RollbackAsync(cancellationToken);
                return SendFriendRequestOutcome.AlreadyFriends;
            }

            if (await RequestExistsAsync(connection, tx, from, to, cancellationToken))
            {
                await tx.RollbackAsync(cancellationToken);
                return SendFriendRequestOutcome.AlreadySent;
            }

            if (await RequestExistsAsync(connection, tx, to, from, cancellationToken))
            {
                await LinkFriendsAsync(connection, tx, from, to, cancellationToken);
                await DeleteRequestAsync(connection, tx, to, from, cancellationToken);
                await DeleteRequestAsync(connection, tx, from, to, cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return SendFriendRequestOutcome.Accepted;
            }

            var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            await connection.ExecuteAsync(
                """
                INSERT INTO friend_requests (from_user_id, to_user_id, date_sent)
                VALUES (@from, @to, @date_sent)
                """,
                new { from = from.Value, to = to.Value, date_sent = nowMs },
                tx);

            await tx.CommitAsync(cancellationToken);
            return SendFriendRequestOutcome.Sent;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<FriendRequestMutationOutcome> AcceptRequestAsync(
        SystemId systemId,
        SystemId sourceSystemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = sourceSystemId;

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (await FriendshipExistsAsync(connection, tx, systemId, source, cancellationToken))
            {
                await tx.RollbackAsync(cancellationToken);
                return FriendRequestMutationOutcome.AlreadyFriends;
            }

            if (!await RequestExistsAsync(connection, tx, source, systemId, cancellationToken))
            {
                await tx.RollbackAsync(cancellationToken);
                return FriendRequestMutationOutcome.NotRequested;
            }

            await LinkFriendsAsync(connection, tx, systemId, source, cancellationToken);
            await DeleteRequestAsync(connection, tx, source, systemId, cancellationToken);
            await DeleteRequestAsync(connection, tx, systemId, source, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return FriendRequestMutationOutcome.Ok;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<FriendRequestMutationOutcome> RejectRequestAsync(
        SystemId systemId,
        SystemId sourceSystemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = sourceSystemId;

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (await FriendshipExistsAsync(connection, tx, systemId, source, cancellationToken))
            {
                await tx.RollbackAsync(cancellationToken);
                return FriendRequestMutationOutcome.AlreadyFriends;
            }

            if (!await RequestExistsAsync(connection, tx, source, systemId, cancellationToken))
            {
                await tx.RollbackAsync(cancellationToken);
                return FriendRequestMutationOutcome.NotRequested;
            }

            await DeleteRequestAsync(connection, tx, source, systemId, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return FriendRequestMutationOutcome.Ok;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<FriendRequestMutationOutcome> CancelRequestAsync(
        SystemId systemId,
        SystemId targetSystemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = targetSystemId;

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (await FriendshipExistsAsync(connection, tx, systemId, target, cancellationToken))
            {
                await tx.RollbackAsync(cancellationToken);
                return FriendRequestMutationOutcome.AlreadyFriends;
            }

            if (!await RequestExistsAsync(connection, tx, systemId, target, cancellationToken))
            {
                await tx.RollbackAsync(cancellationToken);
                return FriendRequestMutationOutcome.NotRequested;
            }

            await DeleteRequestAsync(connection, tx, systemId, target, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return FriendRequestMutationOutcome.Ok;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<SystemId>> DeleteAllForSystemAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var work = await SqliteWork.OpenAsync(_connectionFactory, cancellationToken);
        var friendKeys = (await work.Connection.QueryAsync<string>(
            "SELECT friend_id FROM friendships WHERE user_id = @user_id",
            new { user_id = systemId.Value },
            work.Transaction)).ToArray();

        await work.Connection.ExecuteAsync(
            """
            DELETE FROM friendships
            WHERE user_id = @user_id OR friend_id = @user_id
            """,
            new { user_id = systemId.Value },
            work.Transaction);

        await work.Connection.ExecuteAsync(
            """
            DELETE FROM friend_requests
            WHERE from_user_id = @user_id OR to_user_id = @user_id
            """,
            new { user_id = systemId.Value },
            work.Transaction);

        await work.CommitAsync(cancellationToken);
        return [.. friendKeys.Select(x => new SystemId(x))];
    }

    private static FriendshipReadModel MapFriendship(FriendshipRow row)
    {
        var friendId = new SystemId(row.FriendId);
        var level = ((short)row.Level).FromCode<FriendshipLevel>();
        var since = DateTimeOffset.FromUnixTimeMilliseconds(row.Since);
        return new FriendshipReadModel(
            new FriendProfileReadModel(friendId, null, null, null, null, null),
            new FriendshipModel(level, since),
            Array.Empty<FriendFrontingReadModel>());
    }

    private static FriendRequestReadModel MapRequest(FriendRequestRow row)
    {
        var otherId = new SystemId(row.OtherUserId);
        var dateSent = DateTimeOffset.FromUnixTimeMilliseconds(row.DateSent);
        return new FriendRequestReadModel(
            new FriendProfileReadModel(otherId, null, null, null, null, null),
            new FriendshipRequestModel(dateSent));
    }

    private static async Task<bool> FriendshipExistsAsync(
        SqliteConnection connection,
        DbTransaction tx,
        SystemId userId,
        SystemId friendId,
        CancellationToken cancellationToken)
    {
        var hit = await connection.ExecuteScalarAsync(
            """
            SELECT 1 FROM friendships
            WHERE user_id = @user_id AND friend_id = @friend_id
            LIMIT 1
            """,
            new { user_id = userId.Value, friend_id = friendId.Value },
            tx);
        return hit is not null and not DBNull;
    }

    private static async Task<bool> RequestExistsAsync(
        SqliteConnection connection,
        DbTransaction tx,
        SystemId fromUserId,
        SystemId toUserId,
        CancellationToken cancellationToken)
    {
        var hit = await connection.ExecuteScalarAsync(
            """
            SELECT 1 FROM friend_requests
            WHERE from_user_id = @from AND to_user_id = @to
            LIMIT 1
            """,
            new { from = fromUserId.Value, to = toUserId.Value },
            tx);
        return hit is not null and not DBNull;
    }

    private async Task LinkFriendsAsync(
        SqliteConnection connection,
        DbTransaction tx,
        SystemId left,
        SystemId right,
        CancellationToken cancellationToken)
    {
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var level = (short)FriendshipLevel.Friend;
        await UpsertFriendshipEdgeAsync(connection, tx, left, right, level, nowMs, cancellationToken);
        await UpsertFriendshipEdgeAsync(connection, tx, right, left, level, nowMs, cancellationToken);
    }

    private static Task UpsertFriendshipEdgeAsync(
        SqliteConnection connection,
        DbTransaction tx,
        SystemId userId,
        SystemId friendId,
        short level,
        long sinceMs,
        CancellationToken cancellationToken)
        => connection.ExecuteAsync(
            """
            INSERT INTO friendships (user_id, friend_id, level, since)
            VALUES (@user_id, @friend_id, @level, @since)
            ON CONFLICT(user_id, friend_id) DO UPDATE SET
                level = excluded.level,
                since = excluded.since
            """,
            new { user_id = userId.Value, friend_id = friendId.Value, level, since = sinceMs },
            tx);

    private static async Task<bool> DeleteFriendshipEdgeAsync(
        SqliteConnection connection,
        DbTransaction tx,
        SystemId userId,
        SystemId friendId,
        CancellationToken cancellationToken)
    {
        var removed = await connection.ExecuteAsync(
            """
            DELETE FROM friendships
            WHERE user_id = @user_id AND friend_id = @friend_id
            """,
            new { user_id = userId.Value, friend_id = friendId.Value },
            tx);
        return removed > 0;
    }

    private static Task DeleteRequestAsync(
        SqliteConnection connection,
        DbTransaction tx,
        SystemId fromUserId,
        SystemId toUserId,
        CancellationToken cancellationToken)
        => connection.ExecuteAsync(
            """
            DELETE FROM friend_requests
            WHERE from_user_id = @from AND to_user_id = @to
            """,
            new { from = fromUserId.Value, to = toUserId.Value },
            tx);

    private sealed record FriendshipRow(string FriendId, long Level, long Since);

    private sealed record FriendRequestRow(string OtherUserId, long DateSent);
}
