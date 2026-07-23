using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Domain.Abstractions;

/// <summary>In-process (eventually cluster-wide) pub/sub for domain events (mirrors
/// legacy Phoenix.PubSub).</summary>
public interface IClusterEventBus
{
    /// <summary>Publishes <paramref name="evt"/> to every subscriber of
    /// <typeparamref name="TEvent"/>. Subscribers scoped to a
    /// <c>targetSystemId</c> only receive events when <typeparamref name="TEvent"/> implements
    /// <see cref="ITargetedClusterEvent"/> and the event's target matches.</summary>
    ValueTask PublishAsync<TEvent>(TEvent evt, CancellationToken ct = default)
        where TEvent : class;

    /// <summary>Broadcast subscribe — yields every event of <typeparamref name="TEvent"/>.
    /// Stream completes on <paramref name="ct"/> cancellation.</summary>
    IAsyncEnumerable<TEvent> SubscribeAsync<TEvent>(CancellationToken ct = default)
        where TEvent : class
        => SubscribeAsync<TEvent>(targetSystemId: null, ct);

    /// <summary>Target-scoped subscribe. Non-null <paramref name="targetSystemId"/> filters
    /// events implementing <see cref="ITargetedClusterEvent"/> by equality on TargetSystemId;
    /// null delivers everything. Non-targeted event types bypass the filter and are delivered
    /// to every subscriber.</summary>
    IAsyncEnumerable<TEvent> SubscribeAsync<TEvent>(
        ScopedSystemId? targetSystemId,
        CancellationToken ct = default)
        where TEvent : class;
}
