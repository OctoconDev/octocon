using Interfold.Contracts.Events;
using Interfold.Contracts.Ids;
using Interfold.Domain.Abstractions;

namespace Interfold.Domain.Friendships;

/// <summary>
/// Intent-named pair-publish helpers on <see cref="IClusterEventBus"/> for friendship
/// events. Extensions so command-handler bodies read as
/// <c>_eventBus.PublishFriendshipAddedBothWaysAsync(from, to, ct)</c> — the bus, the
/// noun the caller cares about, sits at the front instead of being buried as the first
/// argument to a static method.
///
/// <para>
/// Every method fans a pair of events (one per direction / one per event-name pair) so
/// the two ends of a friendship or friend-request always move together on the bus. The
/// alternative was for each handler to open-code the two <c>eventBus.PublishAsync</c>
/// calls and mis-order or drop-one under refactor pressure — this centralises the
/// ordering.
/// </para>
///
/// <para>
/// Publish-only, side-effect-only. Nothing here touches idempotency, DB, or command
/// orchestration; those live in <see cref="FriendshipCommandFlow"/>.
/// </para>
/// </summary>
internal static class FriendshipEventBusExtensions
{
    /// <summary>
    /// Publishes <see cref="FriendshipAddedEvent"/> for both sides of a new friendship,
    /// so each system's socket subscribers see the peer appear at the same tick.
    /// </summary>
    public static async ValueTask PublishFriendshipAddedBothWaysAsync(
        this IClusterEventBus eventBus,
        ScopedSystemId from,
        ScopedSystemId to,
        CancellationToken cancellationToken = default)
    {
        await eventBus.PublishAsync(new FriendshipAddedEvent(from, to), cancellationToken);
        await eventBus.PublishAsync(new FriendshipAddedEvent(to, from), cancellationToken);
    }

    /// <summary>
    /// Publishes <see cref="FriendshipRemovedEvent"/> for both sides of a removed
    /// friendship — mirror of <see cref="PublishFriendshipAddedBothWaysAsync"/>.
    /// </summary>
    public static async ValueTask PublishFriendshipRemovedBothWaysAsync(
        this IClusterEventBus eventBus,
        ScopedSystemId from,
        ScopedSystemId to,
        CancellationToken cancellationToken = default)
    {
        await eventBus.PublishAsync(new FriendshipRemovedEvent(from, to), cancellationToken);
        await eventBus.PublishAsync(new FriendshipRemovedEvent(to, from), cancellationToken);
    }

    /// <summary>
    /// Publishes <see cref="FriendRequestRemovedFromEvent"/> then
    /// <see cref="FriendRequestRemovedToEvent"/> — the ordering used by the sender-side
    /// cancel path (the sender's outbound goes first, then the recipient's inbound).
    /// </summary>
    public static async ValueTask PublishRequestRemovedFromThenToAsync(
        this IClusterEventBus eventBus,
        ScopedSystemId from,
        ScopedSystemId to,
        CancellationToken cancellationToken = default)
    {
        await eventBus.PublishAsync(new FriendRequestRemovedFromEvent(from, to), cancellationToken);
        await eventBus.PublishAsync(new FriendRequestRemovedToEvent(to, from), cancellationToken);
    }

    /// <summary>
    /// Publishes <see cref="FriendRequestRemovedToEvent"/> then
    /// <see cref="FriendRequestRemovedFromEvent"/> — the ordering used by the recipient-side
    /// reject / accept path (the recipient's inbound goes first, then the sender's outbound).
    /// </summary>
    public static async ValueTask PublishRequestRemovedToThenFromAsync(
        this IClusterEventBus eventBus,
        ScopedSystemId to,
        ScopedSystemId from,
        CancellationToken cancellationToken = default)
    {
        await eventBus.PublishAsync(new FriendRequestRemovedToEvent(to, from), cancellationToken);
        await eventBus.PublishAsync(new FriendRequestRemovedFromEvent(from, to), cancellationToken);
    }
}
