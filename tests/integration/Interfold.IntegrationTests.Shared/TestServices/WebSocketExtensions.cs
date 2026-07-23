using System.Net.WebSockets;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Ids;
using Microsoft.AspNetCore.TestHost;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// Test-side conveniences for the raw <see cref="WebSocket"/> API. Test code sends thousands of
/// Phoenix frames through <see cref="WebSocket.SendAsync(ArraySegment{byte},WebSocketMessageType,bool,CancellationToken)"/>
/// with the same <c>WebSocketMessageType.Text, endOfMessage: true</c> arguments every time; the
/// extension collapses that transport ceremony so tests read as intent (send frame X) rather than
/// as an implementation of the WebSocket contract.
/// </summary>
public static class WebSocketExtensions
{
    /// <summary>
    /// Sends <paramref name="frame"/> as a single-shot text WebSocket frame. Every Phoenix frame
    /// in this codebase is text-mode + end-of-message; production paths route through
    /// <c>WebSocketEvents.SendPhoenixPushAsync</c> which uses the same pair, so tests should not
    /// be able to accidentally introduce a binary or multi-part frame that wouldn't match a
    /// production sender.
    /// </summary>
    public static Task SendTextFrameAsync(this WebSocket ws, byte[] frame, CancellationToken ct)
        => ws.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, ct);

    public static Task CloseTestDoneAsync(this WebSocket ws, CancellationToken token)
        => ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token);

    public static async Task CloseTestDoneAsync(WebSocket a, WebSocket b, CancellationToken token)
    {
        await a.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token).ConfigureAwait(false);
        await b.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", token).ConfigureAwait(false);
    }

    public static Task<ReceivedPhxFrame?> ReceiveEventFrameAsync(
        this WebSocket ws,
        CancellationToken token,
        string expectedEvent,
        int maxFrames = 5,
        int perFrameTimeoutSeconds = 5)
        => ReceivedPhxFrame.ReceiveEventFrameAsync(ws, token, expectedEvent, maxFrames, perFrameTimeoutSeconds);

    public static async Task<(ReceivedPhxFrame? Reply, ReceivedPhxFrame? Push)> SendEndpointAndCaptureAsync(
        this WebSocket ws,
        byte[] frame,
        string expectedPushEvent,
        CancellationToken token)
    {
        await ws.SendTextFrameAsync(frame, token).ConfigureAwait(false);
        return await ReceivedPhxFrame.ReceiveReplyAndPushAsync(ws, token, expectedPushEvent).ConfigureAwait(false);
    }

    // Extracted from WebSocketTests during the Phase-6 test-project split so Shared's
    // WebSocketHarness can drive the join handshake without a circular ref back into
    // Interfold.Socket.IntegrationTests.
    public static Uri WebSocketBasePath(TestServer server)
        => new($"wss://{server.BaseAddress.Host}");

    public static async Task JoinTopicAsync(WebSocket ws, string topic, string socketToken, CancellationToken timeoutToken)
    {
        var joinFrame = new PhxFrame<PhxJoinPayload>
        {
            Topic = topic,
            Event = "phx_join",
            Payload = new PhxJoinPayload { Token = new SocketToken(socketToken), IsReconnect = true },
            Ref = "1",
            JoinRef = "1"
        };

        await ws.SendTextFrameAsync(joinFrame.ToBytes(), timeoutToken);
        var frame = await ReceivedPhxFrame.ReceiveAsync(ws, timeoutToken);
        var reply = frame.Reply<object>();
        if (reply.Status != PhoenixReplyStatus.Ok)
            throw new InvalidOperationException($"JoinTopicAsync failed for topic '{topic}'. Status: {reply.Status}");
    }
}
