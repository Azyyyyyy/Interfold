using Interfold.Polls.Contracts.Ids;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Polls.Contracts.Events;

public sealed record PollCreatedEvent(SystemId TargetSystemId, PollId PollId) : ITargetedClusterEvent;

public sealed record PollUpdatedEvent(SystemId TargetSystemId, PollId PollId) : ITargetedClusterEvent;

public sealed record PollDeletedEvent(SystemId TargetSystemId, PollId PollId) : ITargetedClusterEvent;
