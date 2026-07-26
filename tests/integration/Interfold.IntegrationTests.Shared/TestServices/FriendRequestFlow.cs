using System.Net.WebSockets;
using Interfold.Shared.Contracts;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// Wire-level drivers for the friend-request Phoenix endpoint. The five WebSocket integration
/// tests that exercise the send/accept/reject/cancel/remove flows all begin with the identical
/// four-line preamble — build the <c>PUT /api/friend-requests/{recipient}</c> endpoint frame,
/// push it on the sender socket, drain the sender ack + push, then wait for the recipient push.
/// <see cref="FriendTrustUntrustFlow"/> starts the same way before it forks into the trust /
/// untrust wire drive. Consolidating the preamble here means every caller stays byte-identical
/// with production behaviour even if the frame shape changes.
/// </summary>
public static class FriendRequestFlow
{
    /// <summary>
    /// Drives the "send friend request → drain sender ack + push → drain recipient push"
    /// sequence from a pair of already-joined sockets. Returns the drained frames so the caller
    /// keeps ownership of the scenario-specific assertions — some tests only need to confirm the
    /// recipient event landed, others also inspect the sender's ack / push pair (or annotate the
    /// assertion with a contextual suffix).
    /// </summary>
    public static async Task<(ReceivedPhxFrame? SenderAck, ReceivedPhxFrame? SenderPush, ReceivedPhxFrame? RecipientEvent)> SendAndDrainAsync(
        WebSocket senderWs,
        string senderSystemId,
        WebSocket recipientWs,
        string recipientSystemId,
        string refId,
        CancellationToken token,
        int recipientMaxFrames = 3)
    {
        var frame = PhxEndpointFrame.Build(
            "system:" + senderSystemId,
            "PUT",
            "/api/friend-requests/" + recipientSystemId,
            new object(),
            refId);
        var (ack, push) = await senderWs.SendEndpointAndCaptureAsync(frame, SocketEventNames.Friendships.RequestSent, token);
        var recipientEvent = await ReceivedPhxFrame.ReceiveEventFrameAsync(
            recipientWs, token, SocketEventNames.Friendships.RequestReceived, maxFrames: recipientMaxFrames);
        return (ack, push, recipientEvent);
    }
}
