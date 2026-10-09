using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Alters.Contracts.Events;

public sealed record AlterCreatedEvent(SystemId TargetSystemId, AlterId AlterId) : ITargetedClusterEvent;

public sealed record AlterUpdatedEvent(SystemId TargetSystemId, AlterId AlterId) : ITargetedClusterEvent;

public sealed record AlterDeletedEvent(SystemId TargetSystemId, AlterId AlterId) : ITargetedClusterEvent;
