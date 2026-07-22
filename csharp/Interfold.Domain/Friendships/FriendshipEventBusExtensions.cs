using Interfold.Contracts.Events;
using Interfold.Contracts.Ids;
using Interfold.Domain.Abstractions;

namespace Interfold.Domain.Friendships;

// Intent-named pair-publish helpers on IClusterEventBus for friendship events. Each
// method fans a fixed-order pair so both ends of a friendship / request always move
// together on the bus. Publish-only — orchestration lives in FriendshipCommandFlow.
internal static class FriendshipEventBusExtensions
{
    public static async ValueTask PublishFriendshipAddedBothWaysAsync(
        this IClusterEventBus eventBus,
        ScopedSystemId from,
        ScopedSystemId to,
        CancellationToken cancellationToken = default)
    {
        await eventBus.PublishAsync(new FriendshipAddedEvent(from, to), cancellationToken);
        await eventBus.PublishAsync(new FriendshipAddedEvent(to, from), cancellationToken);
    }

    public static async ValueTask PublishFriendshipRemovedBothWaysAsync(
        this IClusterEventBus eventBus,
        ScopedSystemId from,
        ScopedSystemId to,
        CancellationToken cancellationToken = default)
    {
        await eventBus.PublishAsync(new FriendshipRemovedEvent(from, to), cancellationToken);
        await eventBus.PublishAsync(new FriendshipRemovedEvent(to, from), cancellationToken);
    }

    // Sender-cancel ordering: sender's outbound first, then recipient's inbound.
    public static async ValueTask PublishRequestRemovedFromThenToAsync(
        this IClusterEventBus eventBus,
        ScopedSystemId from,
        ScopedSystemId to,
        CancellationToken cancellationToken = default)
    {
        await eventBus.PublishAsync(new FriendRequestRemovedFromEvent(from, to), cancellationToken);
        await eventBus.PublishAsync(new FriendRequestRemovedToEvent(to, from), cancellationToken);
    }

    // Recipient-reject/accept ordering: recipient's inbound first, then sender's outbound.
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
