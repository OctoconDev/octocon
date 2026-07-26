namespace Interfold.Socket.Contracts;

/// <summary>
/// Phoenix-protocol transport event and topic names, mirroring the
/// <see cref="SocketEventNames"/> pattern for application push events. These are the
/// framing vocabulary the Kotlin client's Phoenix socket implementation speaks — the
/// spellings are frozen.
/// </summary>
public static class PhoenixEventNames
{
    public const string Heartbeat = "heartbeat";
    public const string Join = "phx_join";
    public const string Reply = "phx_reply";

    /// <summary>Interfold extension event: HTTP relay over the socket.</summary>
    public const string Endpoint = "endpoint";

    /// <summary>The default control topic (heartbeats).</summary>
    public const string PhoenixTopic = "phoenix";
}
