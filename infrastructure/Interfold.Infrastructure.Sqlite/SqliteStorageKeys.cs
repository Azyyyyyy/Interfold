using Interfold.Friendships.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Infrastructure.Sqlite;

/// <summary>Persist prefix-stripped system ids. Read-model ids match expected region-scoped format
/// <c>NormalizeSystemId</c> (bare). Auth/identity returns use
/// <see cref="ToScopedPrincipal"/> so JWT mint can <see cref="ScopedSystemId.ParseScoped"/>.</summary>
internal static class SqliteStorageKeys
{
    private const ScyllaKeyspace WireRegion = ScyllaKeyspace.Nam;

    public static SystemId Normalize(SystemId systemId)
        => new(ScopedSystemId.StripRegionPrefix(systemId.Value));

    public static string Persist(SystemId systemId)
        => ScopedSystemId.StripRegionPrefix(systemId.Value);

    public static SystemId ToWire(string persisted)
        => new(ScopedSystemId.StripRegionPrefix(persisted));

    public static SystemId ToWire(SystemId persistedOrScoped)
        => ToWire(Persist(persistedOrScoped));

    public static SystemId ToScopedPrincipal(string persisted)
        => ScopedSystemId.Compose(WireRegion, ScopedSystemId.StripRegionPrefix(persisted)).AsSystemId();

    public static SystemId ToScopedPrincipal(SystemId persistedOrScoped)
        => ToScopedPrincipal(Persist(persistedOrScoped));

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
