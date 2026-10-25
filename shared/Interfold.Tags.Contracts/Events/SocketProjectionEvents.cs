using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Ids;
using Interfold.Tags.Contracts.Ids;

namespace Interfold.Tags.Contracts.Events;

// Extracted from Interfold.Shared.Contracts/Events/SocketProjectionEvents.cs during Phase-3 Tags
// migration. Other feature-scoped events (Alter*, Poll*, Journal*, Friendship*, Settings*)
// migrate with their respective features.

public sealed record TagCreatedEvent(SystemId TargetSystemId, TagId TagId) : ITargetedClusterEvent;

public sealed record TagUpdatedEvent(SystemId TargetSystemId, TagId TagId) : ITargetedClusterEvent;

public sealed record TagDeletedEvent(SystemId TargetSystemId, TagId TagId) : ITargetedClusterEvent;
