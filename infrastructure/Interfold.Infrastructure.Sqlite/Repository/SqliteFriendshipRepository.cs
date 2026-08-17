using Interfold.Friendships.Contracts.Ids;
using Interfold.Friendships.Contracts.Models.Read;
using Interfold.Friendships.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Microsoft.Data.Sqlite;

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

        var normalizedSystemId = SqliteStorageKeys.Normalize(systemId).Value;
        var normalizedViewerId = SqliteStorageKeys.Normalize(viewerSystemId.Value).Value;

        if (normalizedSystemId == normalizedViewerId)
        {
            return FriendshipLevel.TrustedFriend;
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT level
            FROM friendships
            WHERE user_id = $user_id AND friend_id = $friend_id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$user_id", normalizedSystemId);
        command.Parameters.AddWithValue("$friend_id", normalizedViewerId);

        var raw = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (raw is null || !raw.TryParseWire<FriendshipLevel>(out var level))
        {
            return null;
        }

        return level;
    }

    public async Task<IReadOnlyList<FriendshipReadModel>> ListFriendshipsAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedSystemId = SqliteStorageKeys.Normalize(systemId).Value;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT friend_id, level, since
            FROM friendships
            WHERE user_id = $user_id
            ORDER BY since DESC
            """;
        command.Parameters.AddWithValue("$user_id", normalizedSystemId);

        var list = new List<FriendshipReadModel>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(ReadFriendship(reader));
        }

        return list;
    }

    public async Task<FriendshipReadModel?> GetFriendshipAsync(
        SystemId systemId,
        SystemId friendSystemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedSystemId = SqliteStorageKeys.Normalize(systemId).Value;
        var normalizedFriendId = SqliteStorageKeys.Normalize(friendSystemId).Value;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT friend_id, level, since
            FROM friendships
            WHERE user_id = $user_id AND friend_id = $friend_id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$user_id", normalizedSystemId);
        command.Parameters.AddWithValue("$friend_id", normalizedFriendId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadFriendship(reader);
    }

    public async Task<bool> RemoveFriendshipAsync(
        SystemId systemId,
        SystemId friendSystemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var left = SqliteStorageKeys.Normalize(systemId).Value;
        var right = SqliteStorageKeys.Normalize(friendSystemId).Value;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
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
        var left = SqliteStorageKeys.Normalize(systemId).Value;
        var right = SqliteStorageKeys.Normalize(friendSystemId).Value;
        var level = (trusted ? FriendshipLevel.TrustedFriend : FriendshipLevel.Friend).ToWire();

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE friendships
            SET level = $level
            WHERE user_id = $user_id AND friend_id = $friend_id
            """;
        command.Parameters.AddWithValue("$level", level);
        command.Parameters.AddWithValue("$user_id", left);
        command.Parameters.AddWithValue("$friend_id", right);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    public async Task<FriendRequestIndexReadModel> GetFriendRequestsAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedSystemId = SqliteStorageKeys.Normalize(systemId).Value;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        var outgoing = new List<FriendRequestReadModel>();
        await using (var outCmd = connection.CreateCommand())
        {
            outCmd.CommandText = """
                SELECT to_user_id, date_sent
                FROM friend_requests
                WHERE from_user_id = $user_id
                ORDER BY date_sent DESC
                """;
            outCmd.Parameters.AddWithValue("$user_id", normalizedSystemId);
            await using var reader = await outCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                outgoing.Add(ReadRequest(reader));
            }
        }

        var incoming = new List<FriendRequestReadModel>();
        await using (var inCmd = connection.CreateCommand())
        {
            inCmd.CommandText = """
                SELECT from_user_id, date_sent
                FROM friend_requests
                WHERE to_user_id = $user_id
                ORDER BY date_sent DESC
                """;
            inCmd.Parameters.AddWithValue("$user_id", normalizedSystemId);
            await using var reader = await inCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                incoming.Add(ReadRequest(reader));
            }
        }

        return new FriendRequestIndexReadModel(incoming, outgoing);
    }

    public async Task<SendFriendRequestOutcome> SendRequestAsync(
        SystemId systemId,
        SystemId targetSystemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var from = SqliteStorageKeys.Normalize(systemId).Value;
        var to = SqliteStorageKeys.Normalize(targetSystemId).Value;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
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
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO friend_requests (from_user_id, to_user_id, date_sent)
                    VALUES ($from, $to, $date_sent)
                    """;
                insert.Parameters.AddWithValue("$from", from);
                insert.Parameters.AddWithValue("$to", to);
                insert.Parameters.AddWithValue("$date_sent", nowMs);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

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
        var self = SqliteStorageKeys.Normalize(systemId).Value;
        var source = SqliteStorageKeys.Normalize(sourceSystemId).Value;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
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
        var self = SqliteStorageKeys.Normalize(systemId).Value;
        var source = SqliteStorageKeys.Normalize(sourceSystemId).Value;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
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
        var self = SqliteStorageKeys.Normalize(systemId).Value;
        var target = SqliteStorageKeys.Normalize(targetSystemId).Value;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
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
        var normalized = SqliteStorageKeys.Normalize(systemId).Value;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var friendIds = new List<SystemId>();
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = tx;
                select.CommandText = "SELECT friend_id FROM friendships WHERE user_id = $user_id";
                select.Parameters.AddWithValue("$user_id", normalized);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    friendIds.Add(new SystemId(reader.GetString(0)));
                }
            }

            await using (var deleteFriends = connection.CreateCommand())
            {
                deleteFriends.Transaction = tx;
                deleteFriends.CommandText = """
                    DELETE FROM friendships
                    WHERE user_id = $user_id OR friend_id = $user_id
                    """;
                deleteFriends.Parameters.AddWithValue("$user_id", normalized);
                await deleteFriends.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var deleteRequests = connection.CreateCommand())
            {
                deleteRequests.Transaction = tx;
                deleteRequests.CommandText = """
                    DELETE FROM friend_requests
                    WHERE from_user_id = $user_id OR to_user_id = $user_id
                    """;
                deleteRequests.Parameters.AddWithValue("$user_id", normalized);
                await deleteRequests.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            return friendIds;
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
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT system_id
            FROM accounts
            WHERE username = $username COLLATE NOCASE
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$username", username);

        var scoped = await command.ExecuteScalarAsync(cancellationToken) as string;
        return scoped is null ? null : SqliteStorageKeys.ToWireSystemId(scoped);
    }

    private static FriendshipReadModel ReadFriendship(SqliteDataReader reader)
    {
        var friendId = new SystemId(reader.GetString(0));
        var levelWire = reader.GetString(1);
        if (!levelWire.TryParseWire<FriendshipLevel>(out var level))
        {
            throw new InvalidOperationException($"Corrupt friendship level '{levelWire}'.");
        }

        var since = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2));
        return new FriendshipReadModel(
            new FriendProfileReadModel(friendId, null, null, null, null, null),
            new FriendshipModel(level, since),
            Array.Empty<FriendFrontingReadModel>());
    }

    private static FriendRequestReadModel ReadRequest(SqliteDataReader reader)
    {
        var otherId = new SystemId(reader.GetString(0));
        var dateSent = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1));
        return new FriendRequestReadModel(
            new FriendProfileReadModel(otherId, null, null, null, null, null),
            new FriendshipRequestModel(dateSent));
    }

    private static async Task<bool> FriendshipExistsAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string userId,
        string friendId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT 1 FROM friendships
            WHERE user_id = $user_id AND friend_id = $friend_id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$user_id", userId);
        command.Parameters.AddWithValue("$friend_id", friendId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> RequestExistsAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string fromUserId,
        string toUserId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT 1 FROM friend_requests
            WHERE from_user_id = $from AND to_user_id = $to
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$from", fromUserId);
        command.Parameters.AddWithValue("$to", toUserId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task LinkFriendsAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string left,
        string right,
        CancellationToken cancellationToken)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var level = FriendshipLevel.Friend.ToWire();
        await UpsertFriendshipEdgeAsync(connection, tx, left, right, level, nowMs, cancellationToken);
        await UpsertFriendshipEdgeAsync(connection, tx, right, left, level, nowMs, cancellationToken);
    }

    private static async Task UpsertFriendshipEdgeAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string userId,
        string friendId,
        string level,
        long sinceMs,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO friendships (user_id, friend_id, level, since)
            VALUES ($user_id, $friend_id, $level, $since)
            ON CONFLICT(user_id, friend_id) DO UPDATE SET
                level = excluded.level,
                since = excluded.since
            """;
        command.Parameters.AddWithValue("$user_id", userId);
        command.Parameters.AddWithValue("$friend_id", friendId);
        command.Parameters.AddWithValue("$level", level);
        command.Parameters.AddWithValue("$since", sinceMs);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> DeleteFriendshipEdgeAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string userId,
        string friendId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            DELETE FROM friendships
            WHERE user_id = $user_id AND friend_id = $friend_id
            """;
        command.Parameters.AddWithValue("$user_id", userId);
        command.Parameters.AddWithValue("$friend_id", friendId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static async Task DeleteRequestAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string fromUserId,
        string toUserId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            DELETE FROM friend_requests
            WHERE from_user_id = $from AND to_user_id = $to
            """;
        command.Parameters.AddWithValue("$from", fromUserId);
        command.Parameters.AddWithValue("$to", toUserId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
