using Interfold.Friendships.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Infrastructure.Sqlite;

/// <summary>Persist prefix-stripped system ids. JWT/socket still require
/// <see cref="ScopedSystemId.ParseScoped"/>, so outbound ids are composed with a
/// fixed <see cref="ScyllaKeyspace.Nam"/> wire tag — not a keyspace.</summary>
internal static class SqliteStorageKeys
{
    private const ScyllaKeyspace WireRegion = ScyllaKeyspace.Nam;

    public static SystemId Normalize(SystemId systemId)
        => new(ScopedSystemId.StripRegionPrefix(systemId.Value));

    public static string Persist(SystemId systemId)
        => ScopedSystemId.StripRegionPrefix(systemId.Value);

    public static SystemId ToWire(string persisted)
        => ScopedSystemId.Compose(WireRegion, persisted).AsSystemId();

    public static SystemId ToWire(SystemId persistedOrScoped)
        => ToWire(Persist(persistedOrScoped));

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
