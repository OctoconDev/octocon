namespace Interfold.Shared.Contracts;

/// <summary>
/// Typed shape of the <c>endpoint</c> Phoenix frame payload. <c>Body</c> is a raw
/// JSON string (Phoenix wraps the inner API request body as a JSON-string field so
/// it can be forwarded to the loopback API without being re-serialized — see the
/// forwarding comment in <c>WebSocketHandler.HandleEndpointProxyAsync</c>).
/// </summary>
public sealed record SocketEndpointProxyRequest(string? Method, string? Path, string? Body);
