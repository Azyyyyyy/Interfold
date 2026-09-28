using Dapper;
using Interfold.Friendships.Contracts.Ids;
using Interfold.Friendships.Contracts.Models.Read;
using Interfold.Friendships.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Microsoft.Data.Sqlite;
using System.Data.Common;

namespace Interfold.Infrastructure.Sqlite.Repository;

public sealed class SqliteFriendshipRepository(ISqliteConnectionFactory connectionFactory) : IFriendshipRepository
{
    public async Task<SystemId?> ResolveUserIdAsync(FriendLookup lookup, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return lookup.Kind switch
        {
            FriendLookupKind.Id => SqliteStorageKeys.Normalize(new SystemId(lookup.Value)),
            FriendLookupKind.Username => await ResolveByUsernameAsync(lookup.Value, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(lookup), lookup.Kind,
                $"Unhandled FriendLookupKind '{lookup.Kind}' in ResolveUserIdAsync."),
        };
    }

    public async Task<FriendshipLevel?> GetFriendshipLevelAsync(
        SystemId systemId,
        SystemId? viewerSystemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(viewerSystemId?.Value))
        {
            return null;
        }

        var userKey = SqliteStorageKeys.Persist(systemId);
        var viewerKey = SqliteStorageKeys.Persist(viewerSystemId.Value);

        if (userKey == viewerKey)
        {
            return FriendshipLevel.TrustedFriend;
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var raw = await connection.ExecuteScalarAsync(
            """
            SELECT level
            FROM friendships
            WHERE user_id = @user_id AND friend_id = @friend_id
            LIMIT 1
            """,
            new { user_id = userKey, friend_id = viewerKey });
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
        var userKey = SqliteStorageKeys.Persist(systemId);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<FriendshipRow>(
            """
            SELECT friend_id AS FriendId, level AS Level, since AS Since
            FROM friendships
            WHERE user_id = @user_id
            ORDER BY since DESC
            """,
            new { user_id = userKey });

        return rows.Select(MapFriendship).ToArray();
    }

    public async Task<FriendshipReadModel?> GetFriendshipAsync(
        SystemId systemId,
        SystemId friendSystemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QueryFirstOrDefaultAsync<FriendshipRow>(
            """
            SELECT friend_id AS FriendId, level AS Level, since AS Since
            FROM friendships
            WHERE user_id = @user_id AND friend_id = @friend_id
            LIMIT 1
            """,
            new
            {
                user_id = SqliteStorageKeys.Persist(systemId),
                friend_id = SqliteStorageKeys.Persist(friendSystemId),
            });
        return row is null ? null : MapFriendship(row);
    }

    public async Task<bool> RemoveFriendshipAsync(
        SystemId systemId,
        SystemId friendSystemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var left = SqliteStorageKeys.Persist(systemId);
        var right = SqliteStorageKeys.Persist(friendSystemId);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var removed = await DeleteFriendshipEdgeAsync(connection, tx, left, right, cancellationToken);
            if (!removed)
            {
                await tx.RollbackAsync(cancellationToken);
                return false;
            }

            await DeleteFriendshipEdgeAsync(connection, tx, right, left, cancellationToken);
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

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(
            """
            UPDATE friendships
            SET level = @level
            WHERE user_id = @user_id AND friend_id = @friend_id
            """,
            new
            {
                level,
                user_id = SqliteStorageKeys.Persist(systemId),
                friend_id = SqliteStorageKeys.Persist(friendSystemId),
            });
        return affected > 0;
    }

    public async Task<FriendRequestIndexReadModel> GetFriendRequestsAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userKey = SqliteStorageKeys.Persist(systemId);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        var outgoingRows = await connection.QueryAsync<FriendRequestRow>(
            """
            SELECT to_user_id AS OtherUserId, date_sent AS DateSent
            FROM friend_requests
            WHERE from_user_id = @user_id
            ORDER BY date_sent DESC
            """,
            new { user_id = userKey });

        var incomingRows = await connection.QueryAsync<FriendRequestRow>(
            """
            SELECT from_user_id AS OtherUserId, date_sent AS DateSent
            FROM friend_requests
            WHERE to_user_id = @user_id
            ORDER BY date_sent DESC
            """,
            new { user_id = userKey });

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
        var from = SqliteStorageKeys.Persist(systemId);
        var to = SqliteStorageKeys.Persist(targetSystemId);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
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

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await connection.ExecuteAsync(
                """
                INSERT INTO friend_requests (from_user_id, to_user_id, date_sent)
                VALUES (@from, @to, @date_sent)
                """,
                new { from, to, date_sent = nowMs },
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
        var self = SqliteStorageKeys.Persist(systemId);
        var source = SqliteStorageKeys.Persist(sourceSystemId);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (await FriendshipExistsAsync(connection, tx, self, source, cancellationToken))
            {
                await tx.RollbackAsync(cancellationToken);
                return FriendRequestMutationOutcome.AlreadyFriends;
            }

            if (!await RequestExistsAsync(connection, tx, source, self, cancellationToken))
            {
                await tx.RollbackAsync(cancellationToken);
                return FriendRequestMutationOutcome.NotRequested;
            }

            await LinkFriendsAsync(connection, tx, self, source, cancellationToken);
            await DeleteRequestAsync(connection, tx, source, self, cancellationToken);
            await DeleteRequestAsync(connection, tx, self, source, cancellationToken);
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
        var self = SqliteStorageKeys.Persist(systemId);
        var source = SqliteStorageKeys.Persist(sourceSystemId);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (await FriendshipExistsAsync(connection, tx, self, source, cancellationToken))
            {
                await tx.RollbackAsync(cancellationToken);
                return FriendRequestMutationOutcome.AlreadyFriends;
            }

            if (!await RequestExistsAsync(connection, tx, source, self, cancellationToken))
            {
                await tx.RollbackAsync(cancellationToken);
                return FriendRequestMutationOutcome.NotRequested;
            }

            await DeleteRequestAsync(connection, tx, source, self, cancellationToken);
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
        var self = SqliteStorageKeys.Persist(systemId);
        var target = SqliteStorageKeys.Persist(targetSystemId);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (await FriendshipExistsAsync(connection, tx, self, target, cancellationToken))
            {
                await tx.RollbackAsync(cancellationToken);
                return FriendRequestMutationOutcome.AlreadyFriends;
            }

            if (!await RequestExistsAsync(connection, tx, self, target, cancellationToken))
            {
                await tx.RollbackAsync(cancellationToken);
                return FriendRequestMutationOutcome.NotRequested;
            }

            await DeleteRequestAsync(connection, tx, self, target, cancellationToken);
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
        var userKey = SqliteStorageKeys.Persist(systemId);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var friendKeys = (await connection.QueryAsync<string>(
                "SELECT friend_id FROM friendships WHERE user_id = @user_id",
                new { user_id = userKey },
                tx)).ToArray();

            await connection.ExecuteAsync(
                """
                DELETE FROM friendships
                WHERE user_id = @user_id OR friend_id = @user_id
                """,
                new { user_id = userKey },
                tx);

            await connection.ExecuteAsync(
                """
                DELETE FROM friend_requests
                WHERE from_user_id = @user_id OR to_user_id = @user_id
                """,
                new { user_id = userKey },
                tx);

            await tx.CommitAsync(cancellationToken);
            return friendKeys.Select(SqliteStorageKeys.ToWire).ToArray();
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<SystemId?> ResolveByUsernameAsync(string username, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var persisted = await connection.QueryFirstOrDefaultAsync<string>(
            """
            SELECT system_id
            FROM accounts
            WHERE username = @username COLLATE NOCASE
            LIMIT 1
            """,
            new { username });
        return persisted is null ? null : SqliteStorageKeys.ToWire(persisted);
    }

    private static FriendshipReadModel MapFriendship(FriendshipRow row)
    {
        var friendId = SqliteStorageKeys.ToWire(row.FriendId);
        var level = ((short)row.Level).FromCode<FriendshipLevel>();
        var since = DateTimeOffset.FromUnixTimeMilliseconds(row.Since);
        return new FriendshipReadModel(
            new FriendProfileReadModel(friendId, null, null, null, null, null),
            new FriendshipModel(level, since),
            Array.Empty<FriendFrontingReadModel>());
    }

    private static FriendRequestReadModel MapRequest(FriendRequestRow row)
    {
        var otherId = SqliteStorageKeys.ToWire(row.OtherUserId);
        var dateSent = DateTimeOffset.FromUnixTimeMilliseconds(row.DateSent);
        return new FriendRequestReadModel(
            new FriendProfileReadModel(otherId, null, null, null, null, null),
            new FriendshipRequestModel(dateSent));
    }

    private static async Task<bool> FriendshipExistsAsync(
        SqliteConnection connection,
        DbTransaction tx,
        string userId,
        string friendId,
        CancellationToken cancellationToken)
    {
        var hit = await connection.ExecuteScalarAsync(
            """
            SELECT 1 FROM friendships
            WHERE user_id = @user_id AND friend_id = @friend_id
            LIMIT 1
            """,
            new { user_id = userId, friend_id = friendId },
            tx);
        return hit is not null and not DBNull;
    }

    private static async Task<bool> RequestExistsAsync(
        SqliteConnection connection,
        DbTransaction tx,
        string fromUserId,
        string toUserId,
        CancellationToken cancellationToken)
    {
        var hit = await connection.ExecuteScalarAsync(
            """
            SELECT 1 FROM friend_requests
            WHERE from_user_id = @from AND to_user_id = @to
            LIMIT 1
            """,
            new { from = fromUserId, to = toUserId },
            tx);
        return hit is not null and not DBNull;
    }

    private static async Task LinkFriendsAsync(
        SqliteConnection connection,
        DbTransaction tx,
        string left,
        string right,
        CancellationToken cancellationToken)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var level = (short)FriendshipLevel.Friend;
        await UpsertFriendshipEdgeAsync(connection, tx, left, right, level, nowMs, cancellationToken);
        await UpsertFriendshipEdgeAsync(connection, tx, right, left, level, nowMs, cancellationToken);
    }

    private static Task UpsertFriendshipEdgeAsync(
        SqliteConnection connection,
        DbTransaction tx,
        string userId,
        string friendId,
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
            new { user_id = userId, friend_id = friendId, level, since = sinceMs },
            tx);

    private static async Task<bool> DeleteFriendshipEdgeAsync(
        SqliteConnection connection,
        DbTransaction tx,
        string userId,
        string friendId,
        CancellationToken cancellationToken)
    {
        var removed = await connection.ExecuteAsync(
            """
            DELETE FROM friendships
            WHERE user_id = @user_id AND friend_id = @friend_id
            """,
            new { user_id = userId, friend_id = friendId },
            tx);
        return removed > 0;
    }

    private static Task DeleteRequestAsync(
        SqliteConnection connection,
        DbTransaction tx,
        string fromUserId,
        string toUserId,
        CancellationToken cancellationToken)
        => connection.ExecuteAsync(
            """
            DELETE FROM friend_requests
            WHERE from_user_id = @from AND to_user_id = @to
            """,
            new { from = fromUserId, to = toUserId },
            tx);

    private sealed record FriendshipRow(string FriendId, long Level, long Since);

    private sealed record FriendRequestRow(string OtherUserId, long DateSent);
}
