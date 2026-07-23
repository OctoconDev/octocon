using Interfold.Shared.Contracts;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// Test-side builder for the Phoenix <c>endpoint</c> frame used by the WebSocket integration
/// suite. Every proxied-HTTP-over-WebSocket call is a 6-property literal
/// (<c>Topic</c>, <c>Event = "endpoint"</c>, <c>Payload = new PhxEndpointPayload{ Method, Path, Body }</c>,
/// <c>Ref</c>, <c>JoinRef</c>) that was open-coded verbatim ~20 times across
/// <c>WebSocketTests.cs</c>. Consolidating the shape here means a future
/// wire-format tweak (e.g. moving <c>join_ref</c>) rolls forward through one
/// call site rather than 20+.
/// </summary>
internal static class PhxEndpointFrame
{
    /// <summary>
    /// Build the serialised bytes of an <c>endpoint</c> frame targeting
    /// <paramref name="topic"/>. Defaults to <c>JoinRef = "1"</c> because every
    /// existing call site's join was request-ref <c>"1"</c>; callers with a
    /// different join sequence pass the correct value explicitly.
    /// </summary>
    public static byte[] Build(
        string topic,
        string method,
        string path,
        object body,
        string @ref,
        string? joinRef = "1")
        => new PhxFrame<PhxEndpointPayload>
        {
            Topic = topic,
            Event = "endpoint",
            Payload = new PhxEndpointPayload { Method = method, Path = path, Body = body },
            Ref = @ref,
            JoinRef = joinRef,
        }.ToBytes();
}
