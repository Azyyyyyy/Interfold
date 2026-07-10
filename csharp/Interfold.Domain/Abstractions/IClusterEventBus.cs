using Interfold.Contracts.Events;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Abstractions;

/// <summary>
/// In-process (and eventually cluster-wide) publish/subscribe bus for domain events.
/// <para>
/// Mirrors <c>Phoenix.PubSub</c> used in the legacy Elixir runtime for cache
/// invalidation, fronting flush triggers, and other cross-component signals.
/// </para>
/// </summary>
public interface IClusterEventBus
{
    /// <summary>
    /// Publishes <paramref name="evt"/> to all current subscribers of <typeparamref name="TEvent"/>.
    /// Subscribers that scoped themselves to a specific <c>targetSystemId</c> only receive the event
    /// when <typeparamref name="TEvent"/> implements <see cref="ITargetedClusterEvent"/> and the
    /// event's <see cref="ITargetedClusterEvent.TargetSystemId"/> matches.
    /// </summary>
    ValueTask PublishAsync<TEvent>(TEvent evt, CancellationToken ct = default)
        where TEvent : class;

    /// <summary>
    /// Returns an async stream that yields every event of <typeparamref name="TEvent"/>
    /// published after the subscription is established (broadcast semantics).
    /// The stream completes when <paramref name="ct"/> is cancelled.
    /// </summary>
    IAsyncEnumerable<TEvent> SubscribeAsync<TEvent>(CancellationToken ct = default)
        where TEvent : class
        => SubscribeAsync<TEvent>(targetSystemId: null, ct);

    /// <summary>
    /// Returns an async stream that yields events of <typeparamref name="TEvent"/> scoped to a
    /// specific <paramref name="targetSystemId"/>.
    /// <para>
    /// When <paramref name="targetSystemId"/> is non-null and <typeparamref name="TEvent"/>
    /// implements <see cref="ITargetedClusterEvent"/>, the bus only delivers events whose
    /// <see cref="ITargetedClusterEvent.TargetSystemId"/> equals <paramref name="targetSystemId"/>.
    /// When <paramref name="targetSystemId"/> is null, every event of <typeparamref name="TEvent"/>
    /// is delivered (broadcast semantics, identical to the parameterless overload).
    /// </para>
    /// <para>
    /// Events whose type does not implement <see cref="ITargetedClusterEvent"/> bypass filtering
    /// and are delivered to all subscribers regardless of their scoping value.
    /// </para>
    /// <para>
    /// Round-2 Commit 13 (canvas #24): promoted from <c>SystemId?</c> to <see cref="ScopedSystemId"/>?
    /// so the subscription-side and publisher-side representations are the same shape at
    /// compile time. The publisher-side <see cref="ITargetedClusterEvent.TargetSystemId"/> is
    /// already <see cref="ScopedSystemId"/> (post-Slice-4), so a raw <see cref="SystemId"/>
    /// subscription target had to be normalised at every publish tick through
    /// <c>SystemIdNormalization.StripRegionPrefix</c> to avoid a scoped-vs-raw mismatch. The
    /// asymmetry was documented and load-bearing but was a real bug vector: any subscriber
    /// that ever supplied a scoped <see cref="SystemId"/> (e.g. via
    /// <see cref="ScopedSystemId.AsSystemId"/>) without stripping would silently miss every
    /// delivery. Making both sides speak the scoped composite closes the vector by
    /// construction.
    /// </para>
    /// The stream completes when <paramref name="ct"/> is cancelled.
    /// </summary>
    IAsyncEnumerable<TEvent> SubscribeAsync<TEvent>(
        ScopedSystemId? targetSystemId,
        CancellationToken ct = default)
        where TEvent : class;
}
