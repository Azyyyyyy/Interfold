using Interfold.Friendships.Domain.Abstractions.Repository;
using Interfold.Settings.Contracts.Ids;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Infrastructure.Sqlite.Repository;

public sealed class SqliteNotificationTokenRepository(
    ISqliteConnectionFactory connectionFactory,
    IFriendshipRepository friendshipRepository) : INotificationTokenRepository
{
    public async Task<bool> AddAsync(SystemId systemId, PushToken token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedSystemId = SqliteStorageKeys.Normalize(systemId).Value;
        var normalizedToken = token.Value.Trim();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            // One owner per push token — clear any prior binding first (InMemory overwrite).
            await using (var clear = connection.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM notification_tokens WHERE push_token = $push_token";
                clear.Parameters.AddWithValue("$push_token", normalizedToken);
                await clear.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO notification_tokens (system_id, push_token, inserted_at, updated_at)
                    VALUES ($system_id, $push_token, $inserted_at, $updated_at)
                    ON CONFLICT(system_id, push_token) DO UPDATE SET
                        updated_at = excluded.updated_at
                    """;
                insert.Parameters.AddWithValue("$system_id", normalizedSystemId);
                insert.Parameters.AddWithValue("$push_token", normalizedToken);
                insert.Parameters.AddWithValue("$inserted_at", nowMs);
                insert.Parameters.AddWithValue("$updated_at", nowMs);
                await insert.ExecuteNonQueryAsync(cancellationToken);
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

    public async Task<bool> RemoveAsync(PushToken token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedToken = token.Value.Trim();

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM notification_tokens WHERE push_token = $push_token";
        command.Parameters.AddWithValue("$push_token", normalizedToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<FriendNotificationTokens>> ListTokensForFriendsOfAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSystemId = SqliteStorageKeys.Normalize(systemId);
        if (string.IsNullOrWhiteSpace(normalizedSystemId.Value))
            return Array.Empty<FriendNotificationTokens>();

        var friendships = await friendshipRepository.ListFriendshipsAsync(normalizedSystemId, cancellationToken);
        if (friendships.Count == 0)
            return Array.Empty<FriendNotificationTokens>();

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var groups = new List<FriendNotificationTokens>(friendships.Count);
        var seenFriends = new HashSet<string>(StringComparer.Ordinal);

        foreach (var friendship in friendships)
        {
            if (friendship.Friend is null)
                continue;

            var friendId = SqliteStorageKeys.Normalize(friendship.Friend.Id).Value;
            if (string.IsNullOrWhiteSpace(friendId) || !seenFriends.Add(friendId))
                continue;

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT push_token
                FROM notification_tokens
                WHERE system_id = $system_id
                """;
            command.Parameters.AddWithValue("$system_id", friendId);

            var tokens = new List<PushToken>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var raw = reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(raw))
                    tokens.Add(new PushToken(raw));
            }

            var distinct = tokens.Distinct().ToArray();
            if (distinct.Length == 0)
                continue;

            groups.Add(new FriendNotificationTokens(new SystemId(friendId), distinct));
        }

        return groups;
    }
}
