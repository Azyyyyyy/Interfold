using System.Collections.Concurrent;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Infrastructure.InMemory.Repository;

public sealed class InMemoryNotificationTokenRepository : INotificationTokenRepository
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _tokensBySystem = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _tokenOwners = new(StringComparer.Ordinal);
    private readonly IFriendshipRepository _friendshipRepository;

    public InMemoryNotificationTokenRepository(IFriendshipRepository friendshipRepository)
    {
        _friendshipRepository = friendshipRepository;
    }

    public Task<bool> AddAsync(SystemId systemId, PushToken token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedSystemId = NormalizeSystemId(systemId.Value?.Trim() ?? string.Empty);
        var normalizedToken = token.Value.Trim();
        _tokenOwners[normalizedToken] = normalizedSystemId;

        var systemTokens = _tokensBySystem.GetOrAdd(normalizedSystemId, _ => new(StringComparer.Ordinal));
        systemTokens[normalizedToken] = 1;

        return Task.FromResult(true);
    }

    private static string NormalizeSystemId(SystemId systemId) => InMemoryStorageKeys.NormalizeSystemId(systemId);

    private static string NormalizeSystemId(string systemId) => InMemoryStorageKeys.NormalizeSystemId(systemId);

    public Task<bool> RemoveAsync(PushToken token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedToken = token.Value.Trim();
        if (_tokenOwners.TryRemove(normalizedToken, out var ownerSystemId) &&
            _tokensBySystem.TryGetValue(ownerSystemId, out var systemTokens))
        {
            systemTokens.TryRemove(normalizedToken, out _);
        }

        return Task.FromResult(true);
    }

    // Delegates friend enumeration to IFriendshipRepository, then attaches each friend's
    // token set. Friends with zero registered tokens are omitted so callers can iterate
    // groups without a Count > 0 guard. Mirrors the Scylla impl's shape so backends
    // stay swappable via OCTOCON_PERSISTENCE.
    public async Task<IReadOnlyList<FriendNotificationTokens>> ListTokensForFriendsOfAsync(
        SystemId systemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSystemId = NormalizeSystemId(systemId.Value?.Trim() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalizedSystemId))
            return Array.Empty<FriendNotificationTokens>();

        var friendships = await _friendshipRepository.ListFriendshipsAsync(new SystemId(normalizedSystemId), cancellationToken);
        if (friendships.Count == 0)
            return Array.Empty<FriendNotificationTokens>();

        var groups = new List<FriendNotificationTokens>(friendships.Count);
        var seenFriends = new HashSet<string>(StringComparer.Ordinal);
        foreach (var friendship in friendships)
        {
            var friendId = NormalizeSystemId(friendship.Friend?.Id.Value ?? string.Empty);
            if (string.IsNullOrWhiteSpace(friendId) || !seenFriends.Add(friendId))
                continue;

            if (!_tokensBySystem.TryGetValue(friendId, out var tokens))
                continue;

            var distinctTokens = tokens.Keys
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (distinctTokens.Length == 0)
                continue;

            groups.Add(new FriendNotificationTokens(new SystemId(friendId), distinctTokens));
        }

        return groups;
    }
}
