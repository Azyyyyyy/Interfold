using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Friendships.Contracts.Events;

public sealed record FriendshipAddedEvent(SystemId TargetSystemId, SystemId SystemId) : ITargetedClusterEvent;

public sealed record FriendshipRemovedEvent(SystemId TargetSystemId, SystemId SystemId) : ITargetedClusterEvent;

public sealed record FriendshipTrustedEvent(SystemId TargetSystemId, SystemId SystemId) : ITargetedClusterEvent;

public sealed record FriendshipUntrustedEvent(SystemId TargetSystemId, SystemId SystemId) : ITargetedClusterEvent;

public sealed record FriendRequestSentEvent(SystemId TargetSystemId, SystemId ToSystemId) : ITargetedClusterEvent;

public sealed record FriendRequestReceivedEvent(SystemId TargetSystemId, SystemId FromSystemId) : ITargetedClusterEvent;

public sealed record FriendRequestRemovedFromEvent(SystemId TargetSystemId, SystemId FromSystemId) : ITargetedClusterEvent;

public sealed record FriendRequestRemovedToEvent(SystemId TargetSystemId, SystemId ToSystemId) : ITargetedClusterEvent;
