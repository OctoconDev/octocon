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
    /// Subscribe target and publish target are both <see cref="ScopedSystemId"/> so the
    /// equality filter compares two wire-canonical strings by construction — no risk of a
    /// raw-vs-scoped mismatch silently swallowing every delivery.
    /// </para>
    /// The stream completes when <paramref name="ct"/> is cancelled.
    /// </summary>
    IAsyncEnumerable<TEvent> SubscribeAsync<TEvent>(
        ScopedSystemId? targetSystemId,
        CancellationToken ct = default)
        where TEvent : class;
}
