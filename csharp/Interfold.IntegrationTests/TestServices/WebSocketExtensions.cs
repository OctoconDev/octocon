using System.Net.WebSockets;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// Test-side conveniences for the raw <see cref="WebSocket"/> API. Test code sends thousands of
/// Phoenix frames through <see cref="WebSocket.SendAsync(ArraySegment{byte},WebSocketMessageType,bool,CancellationToken)"/>
/// with the same <c>WebSocketMessageType.Text, endOfMessage: true</c> arguments every time; the
/// extension collapses that transport ceremony so tests read as intent (send frame X) rather than
/// as an implementation of the WebSocket contract.
/// </summary>
internal static class WebSocketExtensions
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
}
