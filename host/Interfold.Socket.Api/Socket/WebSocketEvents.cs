using System.Net.WebSockets;
using System.Text.Json;
using Interfold.Contracts;

namespace Interfold.Api.Socket;

public static class WebSocketEvents
{

public static async Task SendPhoenixPushAsync<TPayload>(
    WebSocket socket,
    string topic,
    string? joinReference,
    string eventName,
    TPayload payload,
    bool replyAsArrayFrame,
    CancellationToken cancellationToken,
    SemaphoreSlim? sendGate = null)
{
    var bytes = replyAsArrayFrame
        ? PhxArrayFrame.CreateBytes(joinReference, null, topic, eventName, payload)
        : new PhxFrame<TPayload>
        {
            Topic = topic,
            Event = eventName,
            Payload = payload,
            Ref = null,
            JoinRef = joinReference
        }.ToBytes();

    if (sendGate is not null)
    {
        await sendGate.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        finally
        {
            sendGate.Release();
        }
    }
    else
    {
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }
}

public static string SerializeSocketJson<T>(this T value) => JsonSerializer.Serialize(value, SocketJson.Options);

}