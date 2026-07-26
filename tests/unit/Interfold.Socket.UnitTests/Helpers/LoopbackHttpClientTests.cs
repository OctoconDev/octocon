using Interfold.Socket.Api.Helpers;

namespace Interfold.Api.UnitTests.Helpers;

// LoopbackHttpClient.IsLoopbackHost — defence-in-depth gate stopping the WebSocket
// endpoint relay from silently downgrading TLS validation on a non-loopback dial-out.
public sealed class LoopbackHttpClientTests
{
    [Test]
    public async Task IPv4Loopback_IsLoopback()
    {
        var result = LoopbackHttpClient.IsLoopbackHost(new Uri("https://127.0.0.1:5101/api/foo"));
        await Assert.That(result).IsTrue()
            .Because("127.0.0.1 is the canonical IPv4 loopback literal used by ResolveLoopbackBaseUri's wildcard rewrite.");
    }

    [Test]
    public async Task IPv4LoopbackEntireRange_IsLoopback()
    {
        var result = LoopbackHttpClient.IsLoopbackHost(new Uri("https://127.42.0.99:5101/api/foo"));
        await Assert.That(result).IsTrue()
            .Because("RFC 3493 / IPAddress.IsLoopback treats the full 127.0.0.0/8 block as loopback.");
    }

    [Test]
    public async Task IPv6Loopback_IsLoopback()
    {
        var result = LoopbackHttpClient.IsLoopbackHost(new Uri("https://[::1]:5101/api/foo"));
        await Assert.That(result).IsTrue()
            .Because("::1 is the IPv6 loopback literal — relevant when the helper resolves a dual-stack binding.");
    }

    [Test]
    public async Task LocalhostHostname_IsLoopback()
    {
        var result = LoopbackHttpClient.IsLoopbackHost(new Uri("http://localhost/api/foo"));
        await Assert.That(result).IsTrue()
            .Because("`localhost` is treated as loopback without DNS resolution so the TestServer fallback works.");
    }

    [Test]
    public async Task OperatorFacingHostname_IsNotLoopback()
    {
        var result = LoopbackHttpClient.IsLoopbackHost(new Uri("https://pineapple.local:5001/api/foo"));
        await Assert.That(result).IsFalse()
            .Because("Operator-facing hostnames are NOT loopback — the permissive validator must never run against them.");
    }

    [Test]
    public async Task LanIp_IsNotLoopback()
    {
        var result = LoopbackHttpClient.IsLoopbackHost(new Uri("http://192.168.1.5:5100/api/foo"));
        await Assert.That(result).IsFalse()
            .Because("Specific NIC binds are reachable but cross-host — TLS validation must remain strict for them.");
    }

    [Test]
    public async Task NullUri_Throws()
    {
        await Assert.That(static () => LoopbackHttpClient.IsLoopbackHost(uri: null!))
            .Throws<ArgumentNullException>()
            .Because("The call site passes a parsed Uri; a null here would be a programming error, not a runtime input.");
    }
}
