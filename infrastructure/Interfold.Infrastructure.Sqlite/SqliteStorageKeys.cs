using Interfold.Friendships.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Infrastructure.Sqlite;

/// <summary>Region-scoped partition keys for Sqlite domain repositories — same
/// <c>{region}:{systemId}</c> shape as InMemory.</summary>
internal static class SqliteStorageKeys
{
    public static SystemId Normalize(SystemId systemId)
        => new(ScopedSystemId.StripRegionPrefix(systemId.Value));

    public static ScopedSystemId ForSystem(IRegionContext regionContext, SystemId systemId)
        => ScopedSystemId.Compose(regionContext.ResolveUserRegion(systemId), systemId);

    public static SystemId ToWireSystemId(string scopedUserId)
        => new(ScopedSystemId.StripRegionPrefix(scopedUserId));

    public static async Task<FriendshipLevel?> ResolveFriendshipLevelAsync(
        SystemId systemId,
        SystemId? viewerSystemId,
        IFriendshipRepository? friendships,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(viewerSystemId?.Value))
        {
            return null;
        }

        if (ScopedSystemId.StripRegionPrefix(systemId) ==
            ScopedSystemId.StripRegionPrefix(viewerSystemId.Value))
        {
            return FriendshipLevel.TrustedFriend;
        }

        if (friendships is null)
        {
            return null;
        }

        return await friendships.GetFriendshipLevelAsync(systemId, viewerSystemId, cancellationToken);
    }
}
