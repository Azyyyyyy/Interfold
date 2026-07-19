using Interfold.Contracts.Events;
using Interfold.Contracts.Ids;
using Interfold.Domain.Abstractions;

namespace Interfold.Domain.Friendships;

internal static class FriendshipEventFlow
{
    public static async ValueTask PublishFriendshipAddedBothWaysAsync(
        IClusterEventBus eventBus,
        ScopedSystemId from,
        ScopedSystemId to,
        CancellationToken cancellationToken = default)
    {
        await eventBus.PublishAsync(new FriendshipAddedEvent(from, to), cancellationToken);
        await eventBus.PublishAsync(new FriendshipAddedEvent(to, from), cancellationToken);
    }

    public static async ValueTask PublishFriendshipRemovedBothWaysAsync(
        IClusterEventBus eventBus,
        ScopedSystemId from,
        ScopedSystemId to,
        CancellationToken cancellationToken = default)
    {
        await eventBus.PublishAsync(new FriendshipRemovedEvent(from, to), cancellationToken);
        await eventBus.PublishAsync(new FriendshipRemovedEvent(to, from), cancellationToken);
    }

    public static async ValueTask PublishRequestRemovedFromThenToAsync(
        IClusterEventBus eventBus,
        ScopedSystemId from,
        ScopedSystemId to,
        CancellationToken cancellationToken = default)
    {
        await eventBus.PublishAsync(new FriendRequestRemovedFromEvent(from, to), cancellationToken);
        await eventBus.PublishAsync(new FriendRequestRemovedToEvent(to, from), cancellationToken);
    }

    public static async ValueTask PublishRequestRemovedToThenFromAsync(
        IClusterEventBus eventBus,
        ScopedSystemId to,
        ScopedSystemId from,
        CancellationToken cancellationToken = default)
    {
        await eventBus.PublishAsync(new FriendRequestRemovedToEvent(to, from), cancellationToken);
        await eventBus.PublishAsync(new FriendRequestRemovedFromEvent(from, to), cancellationToken);
    }
}
