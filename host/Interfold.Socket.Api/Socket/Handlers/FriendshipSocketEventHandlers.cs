using Interfold.Api.Helpers;
using Interfold.Contracts;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Events;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Api.Socket.Handlers;

public static class FriendshipSocketEventHandlers
{
    public static async Task HandleAsync(FriendshipAddedEvent evt, SocketPushContext context, IFriendshipRepository friendshipRepository)
    {
        if (!context.TryGetSystemTopic(evt.TargetSystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var friendship = await GetFriendshipWithRetryAsync(friendshipRepository, evt.TargetSystemId, evt.SystemId, context.CancellationToken);
        // Inlined from the former AvatarUrlQualifier.QualifyFriendship(origin) overload — the
        // shared spine helper moved out during the Phase-3 Friendships slice so
        // Interfold.Api.Shared no longer binds the friendship read-model cluster; per-avatar
        // qualification still uses the shared primitive.
        var qualified = friendship is null
            ? new FriendshipReadModel(
                new FriendProfileReadModel(evt.SystemId, new Username(string.Empty), null, null, string.Empty, new DiscordId(string.Empty)),
                new FriendshipModel(FriendshipLevel.Friend, context.TimeProvider.GetUtcNow()),
                [])
            : friendship with
            {
                Friend = friendship.Friend with
                {
                    AvatarUrl = AvatarUrlQualifier.QualifyAvatar(friendship.Friend, context.RequestOrigin),
                },
                Fronting = friendship.Fronting
                    .Select(f => f with
                    {
                        Alter = f.Alter with
                        {
                            AvatarUrl = AvatarUrlQualifier.QualifyAvatar(f.Alter, context.RequestOrigin),
                        },
                    })
                    .ToArray(),
            };
        
        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Friendships.Added, qualified);
    }

    public static Task HandleAsync(FriendshipRemovedEvent evt, SocketPushContext context)
        => SendAsync(evt.TargetSystemId, SocketEventNames.Friendships.Removed, SocketPayloadPropertyKeys.FriendId, evt.SystemId, context);

    public static Task HandleAsync(FriendshipTrustedEvent evt, SocketPushContext context)
        => SendAsync(evt.TargetSystemId, SocketEventNames.Friendships.Trusted, SocketPayloadPropertyKeys.FriendId, evt.SystemId, context);

    public static Task HandleAsync(FriendshipUntrustedEvent evt, SocketPushContext context)
        => SendAsync(evt.TargetSystemId, SocketEventNames.Friendships.Untrusted, SocketPayloadPropertyKeys.FriendId, evt.SystemId, context);

    public static async Task HandleAsync(FriendRequestSentEvent evt, SocketPushContext context, IFriendshipRepository friendshipRepository)
    {
        await SendRequestPayloadAsync(
            evt.TargetSystemId,
            evt.ToSystemId,
            SocketEventNames.Friendships.RequestSent,
            context,
            friendshipRepository,
            outgoing: true);
    }

    public static async Task HandleAsync(FriendRequestReceivedEvent evt, SocketPushContext context, IFriendshipRepository friendshipRepository)
    {
        await SendRequestPayloadAsync(
            evt.TargetSystemId,
            evt.FromSystemId,
            SocketEventNames.Friendships.RequestReceived,
            context,
            friendshipRepository,
            outgoing: false);
    }

    public static Task HandleAsync(FriendRequestRemovedFromEvent evt, SocketPushContext context)
        => SendAsync(evt.TargetSystemId, SocketEventNames.Friendships.RequestRemoved, SocketPayloadPropertyKeys.SystemId, evt.FromSystemId, context);

    public static Task HandleAsync(FriendRequestRemovedToEvent evt, SocketPushContext context)
        => SendAsync(evt.TargetSystemId, SocketEventNames.Friendships.RequestRemoved, SocketPayloadPropertyKeys.SystemId, evt.ToSystemId, context);

    private static async Task SendRequestPayloadAsync(
        SystemId targetSystemId,
        SystemId otherSystemId,
        string eventName,
        SocketPushContext context,
        IFriendshipRepository friendshipRepository,
        bool outgoing)
    {
        if (!context.TryGetSystemTopic(targetSystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var matched = await GetFriendRequestWithRetryAsync(
            friendshipRepository,
            targetSystemId,
            otherSystemId,
            outgoing,
            context.CancellationToken);

        var payload = matched is null
            ? new FriendRequestSocketPayload(
                new FriendshipRequestModel(context.TimeProvider.GetUtcNow()),
                new FriendProfileReadModel(otherSystemId, new Username(string.Empty), null, null, string.Empty, new DiscordId(string.Empty)))
            : new FriendRequestSocketPayload(
                matched.Request,
                matched.System with { AvatarUrl = AvatarUrlQualifier.QualifyAvatar(matched.System, context.RequestOrigin) });

        await context.SendAsync(topic, joinRef, asArray, eventName, payload);
    }

    private static Task<FriendshipReadModel?> GetFriendshipWithRetryAsync(
        IFriendshipRepository friendshipRepository,
        SystemId targetSystemId,
        SystemId friendSystemId,
        CancellationToken cancellationToken)
        => UntilNotNullAsync(
            () => friendshipRepository.GetFriendshipAsync(targetSystemId, friendSystemId, cancellationToken),
            maxAttempts: 3,
            delayBetween: TimeSpan.FromMilliseconds(50),
            cancellationToken);

    private static Task<FriendRequestReadModel?> GetFriendRequestWithRetryAsync(
        IFriendshipRepository friendshipRepository,
        SystemId targetSystemId,
        SystemId otherSystemId,
        bool outgoing,
        CancellationToken cancellationToken)
        => UntilNotNullAsync(
            async () =>
            {
                var index = await friendshipRepository.GetFriendRequestsAsync(targetSystemId, cancellationToken);
                return (outgoing ? index.Outgoing : index.Incoming)
                    .FirstOrDefault(r => SystemTopic.IdMatches(r.System?.Id, otherSystemId));
            },
            maxAttempts: 10,
            delayBetween: TimeSpan.FromMilliseconds(100),
            cancellationToken);

    /// <summary>
    /// Poll <paramref name="fetch"/> up to <paramref name="maxAttempts"/> times, sleeping
    /// <paramref name="delayBetween"/> between attempts (never after the last), and return
    /// the first non-null result or <c>null</c> if the deadline expires. Consolidates the
    /// friendship-and-request eventual-consistency retry loops so the two socket-handler
    /// paths can't drift on the "return null after the last attempt, don't wait after it"
    /// invariant that a hand-rolled off-by-one on the delay branch could otherwise
    /// re-introduce. Kept private — the socket handlers are the only caller, and callers
    /// outside this file should use the domain-abstraction retries (Polly / RetryHandler)
    /// rather than a naive fixed-delay poll.
    /// </summary>
    private static async Task<T?> UntilNotNullAsync<T>(
        Func<Task<T?>> fetch,
        int maxAttempts,
        TimeSpan delayBetween,
        CancellationToken cancellationToken)
        where T : class
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var result = await fetch();
            if (result is not null)
            {
                return result;
            }

            if (attempt < maxAttempts - 1)
            {
                await Task.Delay(delayBetween, cancellationToken);
            }
        }

        return null;
    }

    private static async Task SendAsync(SystemId targetSystemId, string eventName, string payloadKey, SystemId payloadValue, SocketPushContext context)
    {
        ISocketPayload payload = payloadKey switch
        {
            SocketPayloadPropertyKeys.FriendId => new FriendIdSocketPayload(payloadValue),
            SocketPayloadPropertyKeys.SystemId => new SystemIdSocketPayload(payloadValue),
            _ => throw new InvalidOperationException($"Unrecognized socket payload key '{payloadKey}'.")
        };

        await context.SendIfJoinedAsync(targetSystemId, eventName, payload);
    }

}
