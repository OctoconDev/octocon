namespace Interfold.Api.Socket;

/// <summary>
/// Query-string parameter names on the WebSocket upgrade request. Wire-frozen — the
/// Kotlin client builds the upgrade URI with these exact spellings.
/// </summary>
internal static class SocketQueryKeys
{
    public const string Token = "token";
}

/// <summary>
/// JSON property names inside friendship push payloads, selecting which typed payload
/// record wraps the pushed system id. Wire-frozen — clients match on the member name.
/// </summary>
internal static class SocketPayloadPropertyKeys
{
    public const string FriendId = "friend_id";
    public const string SystemId = "system_id";
}
