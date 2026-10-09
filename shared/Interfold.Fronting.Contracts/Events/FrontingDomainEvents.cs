using Interfold.Fronting.Contracts.Ids;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Fronting.Contracts.Events;

/// <summary>Published to <c>IClusterEventBus</c> on any fronting-state change. Consumed on
/// primary nodes by <c>FrontNotifierBackgroundService</c> for FCM fan-out (Phase N).</summary>
public sealed record FrontingStateChangedEvent(SystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record FrontDeletedEvent(SystemId TargetSystemId, FrontId FrontId) : ITargetedClusterEvent;

/// <summary>Emits socket <c>"fronting_started"</c>.</summary>
public sealed record FrontingStartedEvent(SystemId TargetSystemId, FrontId FrontId) : ITargetedClusterEvent;

/// <summary>Emits socket <c>"fronting_ended"</c>.</summary>
public sealed record FrontingEndedEvent(SystemId TargetSystemId, AlterId AlterId) : ITargetedClusterEvent;

/// <summary>Emits socket <c>"fronting_set"</c>.</summary>
public sealed record FrontingSetEvent(SystemId TargetSystemId, FrontId FrontId) : ITargetedClusterEvent;

/// <summary>Emits socket <c>"fronting_bulk"</c>.</summary>
public sealed record FrontingBulkUpdatedEvent(SystemId TargetSystemId) : ITargetedClusterEvent;

/// <summary>Emits socket <c>"front_updated"</c> after a front comment change.</summary>
public sealed record FrontCommentUpdatedEvent(SystemId TargetSystemId, FrontId FrontId) : ITargetedClusterEvent;

/// <summary>Emits socket <c>"primary_front"</c>.</summary>
public sealed record FrontingPrimaryChangedEvent(SystemId TargetSystemId, AlterId? AlterId) : ITargetedClusterEvent;
