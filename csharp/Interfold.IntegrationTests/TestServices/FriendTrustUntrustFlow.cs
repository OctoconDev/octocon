using System.Net.WebSockets;
using Interfold.Contracts;
using Interfold.IntegrationTests.Endpoints;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// End-to-end wire drive for the "send friend request → accept → trust → untrust" flow used
/// by the WebSocket integration suite. The flow originally lived open-coded in
/// <c>WebSocketTests.Api_UserSocketEndpoint_PushesFriendTrustedAndUntrusted_ToActor</c>;
/// <c>WebSocketThreadStarvationTests.RunFriendTrustUntrustFlowAsync</c> then cloned the
/// entire body verbatim (identical frames, identical assertions) purely so it could run
/// the same flow with the thread-pool throttled, distinguishing the two only by an
/// "under thread starvation" suffix on every <c>Because(...)</c> message.
///
/// <para>
/// This helper unifies both paths: it takes two already-joined sockets (single-party
/// helper <see cref="WebSocketHarness.ConnectAndJoinAsync"/> for either side is fine) and
/// drives the four wire messages in sequence, asserting the expected ack/push pairs after
/// each. Callers pass <paramref name="contextSuffix"/> when they want the assertion
/// messages to name the scenario (e.g. <c>"under thread starvation"</c>); the default
/// empty suffix matches the base <c>WebSocketTests</c> phrasing.
/// </para>
///
/// <para>
/// The refs (<c>"2"</c>–<c>"5"</c>) are hardcoded to preserve behavioural parity with the
/// original inline code, where every ref value already advanced sequentially past the
/// <c>join</c> at <c>"1"</c>. Callers that need a different ref sequence must hand-roll
/// the wire drive.
/// </para>
/// </summary>
internal static class FriendTrustUntrustFlow
{
    public static async Task RunAsync(
        WebSocket senderWs,
        string senderSystemId,
        WebSocket recipientWs,
        string recipientSystemId,
        CancellationToken token,
        string? contextSuffix = null)
    {
        var suffix = string.IsNullOrEmpty(contextSuffix) ? "." : $" {contextSuffix}.";

        var (_, _, recipientReceived) = await FriendRequestFlow.SendAndDrainAsync(
            senderWs, senderSystemId, recipientWs, recipientSystemId, "2", token);
        await Assert.That(recipientReceived).IsNotNull()
            .Because($"Expected friend_request_received before accept{suffix}");

        var acceptFrame = PhxEndpointFrame.Build(
            "system:" + recipientSystemId,
            "POST",
            "/api/friend-requests/" + senderSystemId + "/accept",
            new object(),
            "3");

        await recipientWs.SendTextFrameAsync(acceptFrame, token);

        _ = await ReceivedPhxFrame.ReceiveEventFrameAsync(recipientWs, token, SocketEventNames.Friendships.Added);
        _ = await ReceivedPhxFrame.ReceiveReplyAndPushAsync(senderWs, token, SocketEventNames.Friendships.Added);

        var trustFrame = PhxEndpointFrame.Build(
            "system:" + senderSystemId,
            "POST",
            "/api/friends/" + recipientSystemId + "/trust",
            new object(),
            "4");

        var (trustAck, trustPush) = await senderWs.SendEndpointAndCaptureAsync(trustFrame, SocketEventNames.Friendships.Trusted, token);

        using (Assert.Multiple())
        {
            await Assert.That(trustAck).IsNotNull()
                .Because($"Expected endpoint ack on sender socket for trust{suffix}");
            await Assert.That(trustPush).IsNotNull()
                .Because($"Expected friend_trusted push on sender socket after trust{suffix}");
        }

        if (trustPush is not null)
        {
            await Assert.That(trustPush.Event).IsEqualTo(SocketEventNames.Friendships.Trusted)
                .Because($"Expected friend_trusted event on sender socket{suffix}");
        }

        var untrustFrame = PhxEndpointFrame.Build(
            "system:" + senderSystemId,
            "POST",
            "/api/friends/" + recipientSystemId + "/untrust",
            new object(),
            "5");

        var (untrustAck, untrustPush) = await senderWs.SendEndpointAndCaptureAsync(untrustFrame, SocketEventNames.Friendships.Untrusted, token);

        using (Assert.Multiple())
        {
            await Assert.That(untrustAck).IsNotNull()
                .Because($"Expected endpoint ack on sender socket for untrust{suffix}");
            await Assert.That(untrustPush).IsNotNull()
                .Because($"Expected friend_untrusted push on sender socket after untrust{suffix}");
        }

        if (untrustPush is not null)
        {
            await Assert.That(untrustPush.Event).IsEqualTo(SocketEventNames.Friendships.Untrusted)
                .Because($"Expected friend_untrusted event on sender socket{suffix}");
        }
    }
}
