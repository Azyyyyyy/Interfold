using Dapper;
using Interfold.Friendships.Domain.Abstractions.Repository;
using Interfold.Settings.Contracts.Ids;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Infrastructure.Sqlite.Repository;

public sealed class SqliteNotificationTokenRepository : INotificationTokenRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IFriendshipRepository _friendshipRepository;
    private readonly TimeProvider _timeProvider;

    public SqliteNotificationTokenRepository(
        ISqliteConnectionFactory connectionFactory,
        IFriendshipRepository friendshipRepository,
        TimeProvider timeProvider)
    {
        _connectionFactory = connectionFactory;
        _friendshipRepository = friendshipRepository;
        _timeProvider = timeProvider;
    }

    public async Task<bool> AddAsync(SystemId systemId, PushToken token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedToken = token.Value.Trim();
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.ExecuteAsync(
            """
            INSERT OR REPLACE INTO notification_tokens (system_id, push_token, inserted_at, updated_at)
            VALUES (@system_id, @push_token, @inserted_at, @updated_at)
            """,
            new
            {
                system_id = systemId,
                push_token = normalizedToken,
                inserted_at = nowMs,
                updated_at = nowMs,
            });
        return rows > 0;
    }

    public async Task<bool> RemoveAsync(PushToken token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.ExecuteAsync(
            "DELETE FROM notification_tokens WHERE push_token = @push_token",
            new { push_token = token.Value.Trim() });
        return rows > 0;
    }

    public async Task<IReadOnlyList<FriendNotificationTokens>> ListTokensForFriendsOfAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(systemId.Value))
            return Array.Empty<FriendNotificationTokens>();

        var friendships = await _friendshipRepository.ListFriendshipsAsync(systemId, cancellationToken);
        if (friendships.Count == 0)
            return Array.Empty<FriendNotificationTokens>();

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var groups = new List<FriendNotificationTokens>(friendships.Count);
        var seenFriends = new HashSet<SystemId>();

        foreach (var friendship in friendships)
        {
            if (friendship.Friend is null)
                continue;

            var friendId = friendship.Friend.Id;
            if (string.IsNullOrWhiteSpace(friendId.Value) || !seenFriends.Add(friendId))
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

            groups.Add(new FriendNotificationTokens(friendId, tokens));
        }

        return groups;
    }
}
