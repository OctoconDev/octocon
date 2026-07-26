using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Interfold.Socket.Contracts;

namespace Interfold.IntegrationTests.Shared.TestServices;

/// <summary>
/// A strongly-typed representation of a received Phoenix WebSocket frame.
/// Handles both object-format and array-format frames.
/// </summary>
public sealed record ReceivedPhxFrame(
    string Topic,
    string Event,
    string? Ref,
    string? JoinRef,
    JsonElement? RawPayload)
{
    /// <summary>
    /// Deserialize the raw payload into a strongly-typed payload object.
    /// </summary>
    public TPayload Payload<TPayload>() =>
        RawPayload is null
            ? throw new InvalidOperationException("Frame has no payload.")
            : JsonSerializer.Deserialize<TPayload>(RawPayload.Value.GetRawText(), SocketJson.Options)
              ?? throw new InvalidOperationException($"Failed to deserialize payload as {typeof(TPayload).Name}.");

    /// <summary>
    /// For phx_reply frames: deserialize the payload as PhoenixReplyPayload&lt;TResponse&gt;.
    /// </summary>
    public PhoenixReplyPayload<TResponse> Reply<TResponse>()
    {
        if (!string.Equals(Event, "phx_reply", StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected phx_reply event but got '{Event}'.");

        return Payload<PhoenixReplyPayload<TResponse>>();
    }

    /// <summary>
    /// For endpoint proxy replies: deserialize the response body (which is a JSON string) into a typed object.
    /// </summary>
    public TBody EndpointBody<TBody>()
    {
        var reply = Reply<SocketEndpointProxyResponse>();
        if (string.IsNullOrWhiteSpace(reply.Response.Body))
            throw new InvalidOperationException("Endpoint proxy response body is empty.");

        return JsonSerializer.Deserialize<TBody>(reply.Response.Body, SocketJson.Options)
               ?? throw new InvalidOperationException($"Failed to deserialize endpoint body as {typeof(TBody).Name}.");
    }

    /// <summary>
    /// Parse a raw WebSocket text message into a ReceivedPhxFrame.
    /// Supports both object-format and array-format Phoenix frames.
    /// </summary>
    public static ReceivedPhxFrame Parse(string rawText)
    {
        using var doc = JsonDocument.Parse(rawText);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() >= 5)
        {
            var joinRef = root[0].ValueKind == JsonValueKind.String ? root[0].GetString() : null;
            var @ref = root[1].ValueKind == JsonValueKind.String ? root[1].GetString() : null;
            var topic = root[2].GetString() ?? string.Empty;
            var @event = root[3].GetString() ?? string.Empty;
            var payload = root[4].Clone();

            return new ReceivedPhxFrame(topic, @event, @ref, joinRef, payload);
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            var topic = root.TryGetProperty("topic", out var topicProp) && topicProp.ValueKind == JsonValueKind.String
                ? topicProp.GetString() ?? string.Empty
                : string.Empty;

            var @event = root.TryGetProperty("event", out var eventProp) && eventProp.ValueKind == JsonValueKind.String
                ? eventProp.GetString() ?? string.Empty
                : string.Empty;

            var @ref = root.TryGetProperty("ref", out var refProp) && refProp.ValueKind == JsonValueKind.String
                ? refProp.GetString()
                : null;

            var joinRef = root.TryGetProperty("join_ref", out var joinRefProp) && joinRefProp.ValueKind == JsonValueKind.String
                ? joinRefProp.GetString()
                : null;

            JsonElement? payload = root.TryGetProperty("payload", out var payloadProp)
                ? payloadProp.Clone()
                : null;

            return new ReceivedPhxFrame(topic, @event, @ref, joinRef, payload);
        }

        throw new InvalidOperationException($"Cannot parse Phoenix frame from: {rawText}");
    }

    /// <summary>
    /// Receive a single frame from a WebSocket and parse it into a ReceivedPhxFrame.
    /// </summary>
    public static async Task<ReceivedPhxFrame> ReceiveAsync(WebSocket ws, CancellationToken token, int timeoutSeconds = 30)
    {
        var text = await ReceiveTextAsync(ws, token, timeoutSeconds);
        return Parse(text);
    }

    /// <summary>
    /// Receive a phx_reply frame and return the typed reply payload.
    /// </summary>
    public static async Task<PhoenixReplyPayload<TResponse>> ReceiveReplyAsync<TResponse>(WebSocket ws, CancellationToken token, int timeoutSeconds = 30)
    {
        var frame = await ReceiveAsync(ws, token, timeoutSeconds);
        return frame.Reply<TResponse>();
    }

    /// <summary>
    /// Scan up to maxFrames received frames for one matching the expected event name, and return its typed payload.
    /// Returns null if not found within maxFrames.
    /// </summary>
    public static async Task<TPayload?> ReceiveEventPayloadAsync<TPayload>(
        WebSocket ws,
        CancellationToken token,
        string expectedEvent,
        int maxFrames = 6,
        int perFrameTimeoutSeconds = 30) where TPayload : class
    {
        for (var i = 0; i < maxFrames; i++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(perFrameTimeoutSeconds));

            ReceivedPhxFrame frame;
            try
            {
                frame = await ReceiveAsync(ws, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (string.Equals(frame.Event, expectedEvent, StringComparison.Ordinal))
                return frame.Payload<TPayload>();
        }

        return null;
    }

    /// <summary>
    /// Scan up to maxFrames received frames for one matching the expected event name, and return the frame.
    /// Returns null if not found within maxFrames.
    /// </summary>
    public static async Task<ReceivedPhxFrame?> ReceiveEventFrameAsync(
        WebSocket ws,
        CancellationToken token,
        string expectedEvent,
        int maxFrames = 6,
        int perFrameTimeoutSeconds = 30)
    {
        for (var i = 0; i < maxFrames; i++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(perFrameTimeoutSeconds));

            ReceivedPhxFrame frame;
            try
            {
                frame = await ReceiveAsync(ws, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (string.Equals(frame.Event, expectedEvent, StringComparison.Ordinal))
                return frame;
        }

        return null;
    }

    /// <summary>
    /// Scan up to maxFrames for a frame matching the predicate.
    /// </summary>
    public static async Task<ReceivedPhxFrame?> ReceiveUntilAsync(
        WebSocket ws,
        CancellationToken token,
        Func<ReceivedPhxFrame, bool> predicate,
        int maxFrames = 6,
        int perFrameTimeoutSeconds = 30)
    {
        for (var i = 0; i < maxFrames; i++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(perFrameTimeoutSeconds));

            ReceivedPhxFrame frame;
            try
            {
                frame = await ReceiveAsync(ws, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (predicate(frame))
                return frame;
        }

        return null;
    }

    /// <summary>
    /// Receive frames and collect both the phx_reply and a push event with the given name.
    /// Returns both as a tuple. Either may be null if not received within maxFrames.
    /// </summary>
    public static async Task<(ReceivedPhxFrame? Reply, ReceivedPhxFrame? Push)> ReceiveReplyAndPushAsync(
        WebSocket ws,
        CancellationToken token,
        string expectedPushEvent,
        int maxFrames = 6,
        int perFrameTimeoutSeconds = 30)
    {
        ReceivedPhxFrame? reply = null;
        ReceivedPhxFrame? push = null;

        for (var i = 0; i < maxFrames && (reply is null || push is null); i++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(perFrameTimeoutSeconds));

            ReceivedPhxFrame frame;
            try
            {
                frame = await ReceiveAsync(ws, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (frame.Event == "phx_reply")
                reply = frame;
            else if (string.Equals(frame.Event, expectedPushEvent, StringComparison.Ordinal))
                push = frame;
        }

        return (reply, push);
    }

    /// <summary>
    /// Receive frames and collect the two specified events.
    /// Returns both as a tuple. Either may be null if not received within maxAttempts.
    /// </summary>
    public static async Task<(ReceivedPhxFrame? A, ReceivedPhxFrame? B)> ReceiveTwoOfAsync(
        WebSocket ws, string eventA, string eventB,
        TimeSpan perAttemptTimeout, int maxAttempts, CancellationToken ct)
    {
        ReceivedPhxFrame? a = null;
        ReceivedPhxFrame? b = null;

        for (var i = 0; i < maxAttempts && (a is null || b is null); i++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(perAttemptTimeout);

            ReceivedPhxFrame frame;
            try
            {
                frame = await ReceiveAsync(ws, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (string.Equals(frame.Event, eventA, StringComparison.Ordinal))
                a = frame;
            else if (string.Equals(frame.Event, eventB, StringComparison.Ordinal))
                b = frame;
        }

        return (a, b);
    }

    private static async Task<string> ReceiveTextAsync(WebSocket ws, CancellationToken token, int timeoutSeconds = 30)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var buffer = new byte[8192];
        using var ms = new MemoryStream();

        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException($"WebSocket closed by server. {result.CloseStatusDescription}/{result.CloseStatus}");

            ms.Write(buffer, 0, result.Count);
        } while (result is { EndOfMessage: false, CloseStatus: not null });

        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
