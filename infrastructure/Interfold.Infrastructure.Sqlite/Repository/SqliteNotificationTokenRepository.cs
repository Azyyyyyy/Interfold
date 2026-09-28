using Dapper;
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
        var normalizedSystemId = SqliteStorageKeys.Persist(systemId);
        var normalizedToken = token.Value.Trim();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await connection.ExecuteAsync(
                "DELETE FROM notification_tokens WHERE push_token = @push_token",
                new { push_token = normalizedToken },
                tx);
            await connection.ExecuteAsync(
                """
                INSERT INTO notification_tokens (system_id, push_token, inserted_at, updated_at)
                VALUES (@system_id, @push_token, @inserted_at, @updated_at)
                ON CONFLICT(system_id, push_token) DO UPDATE SET
                    updated_at = excluded.updated_at
                """,
                new
                {
                    system_id = normalizedSystemId,
                    push_token = normalizedToken,
                    inserted_at = nowMs,
                    updated_at = nowMs,
                },
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

    public async Task<bool> RemoveAsync(PushToken token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            "DELETE FROM notification_tokens WHERE push_token = @push_token",
            new { push_token = token.Value.Trim() });
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

            var friendId = SqliteStorageKeys.Persist(friendship.Friend.Id);
            if (string.IsNullOrWhiteSpace(friendId) || !seenFriends.Add(friendId))
                continue;

            var tokens = (await connection.QueryAsync<string>(
                    """
                    SELECT push_token
                    FROM notification_tokens
                    WHERE system_id = @system_id
                    """,
                    new { system_id = friendId }))
                .Where(raw => !string.IsNullOrWhiteSpace(raw))
                .Select(raw => new PushToken(raw))
                .Distinct()
                .ToArray();

            if (tokens.Length == 0)
                continue;

            groups.Add(new FriendNotificationTokens(SqliteStorageKeys.ToWire(friendId), tokens));
        }

        return groups;
    }
}
