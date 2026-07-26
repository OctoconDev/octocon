using System.Net;
using System.Text.Json.Serialization;

namespace Interfold.Socket.Contracts;

// Status is HTTP semantics; JsonNumberEnumConverter pins the historical bare-number wire
// form (SocketJson has no enum policy, but explicit is safer than relying on the default).
public sealed record SocketEndpointProxyResponse(
    [property: JsonConverter(typeof(JsonNumberEnumConverter<HttpStatusCode>))] HttpStatusCode Status,
    string Body) : ISocketPayload;
