namespace Interfold.Socket.Api.Helpers;

/// <summary>Named <see cref="HttpClient"/> for the WS endpoint-relay's self-call. Uses a
/// permissive <c>RemoteCertificateValidationCallback</c>: the leaf PFX served by Kestrel
/// has no loopback SAN and its private root isn't in the container trust store, so both
/// default checks fail. Safe because the call site's <see cref="IsLoopbackHost"/> guard
/// keeps the client pinned to loopback destinations.</summary>
public static class LoopbackHttpClient
{
    /// <summary><see cref="IHttpClientFactory"/> client name. NEVER use for a non-loopback
    /// destination — TLS validation is permissive.</summary>
    public const string Name = "interfold-loopback";

    /// <summary>Literal loopback-shape check (no name resolution): defers to
    /// <see cref="Uri.IsLoopback"/> which covers 127.0.0.0/8, ::1, and <c>localhost</c>.</summary>
    public static bool IsLoopbackHost(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.IsLoopback;
    }
}
