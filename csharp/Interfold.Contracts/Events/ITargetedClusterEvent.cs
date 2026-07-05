namespace Interfold.Contracts.Events;

/// <summary>
/// Marker for cluster events whose primary recipient is a single system identified by
/// <see cref="TargetSystemId"/>.
/// <para>
/// <c>IClusterEventBus</c> uses this contract to filter delivery: a subscription created
/// with a non-null <c>targetSystemId</c> only receives events whose <see cref="TargetSystemId"/>
/// matches that value. Events that do not implement this interface are broadcast to every
/// subscriber regardless of scoping, which preserves correctness for any future non-targeted signals.
/// </para>
/// <para>
/// Slice 4 retyped this field to <see cref="Interfold.Contracts.Ids.ScopedSystemId"/> so the
/// event bus's equality filter (<c>e.TargetSystemId == subscriber.SystemId</c>) is
/// compile-time-guaranteed to compare two wire-canonical strings — the pre-Slice-4 code
/// relied on every publisher hand-formatting a scoped prefix, which was easy to get wrong.
/// </para>
/// </summary>
public interface ITargetedClusterEvent
{
    Interfold.Contracts.Ids.ScopedSystemId TargetSystemId { get; }
}
