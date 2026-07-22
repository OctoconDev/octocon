using Interfold.Api.Socket;

namespace Interfold.Api.UnitTests.Socket;

// Guards against the pre-fix behaviour where the proxy dialed its self-call using the
// inbound request Scheme+Host (operator-facing hostname:port), which crashed in
// container topologies because the container couldn't reach that address. The helper
// resolves from Kestrel's actual bindings instead.
public sealed class ResolveLoopbackBaseUriTests
{
    // Fallback used under TestServer's no-op IServerAddressesFeature. Kestrel always
    // reports its bindings in production so this branch is unreachable there.
    private const string TestServerFallback = "http://localhost";

    [Test]
    public async Task NullAddresses_FallsBackToLocalhost()
    {
        var result = WebSocketHandler.ResolveLoopbackBaseUri(addresses: null);
        await Assert.That(result).IsEqualTo(TestServerFallback)
            .Because("Null Addresses should not crash the proxy — under TestServer's no-op feature the in-memory HttpClient routes by pipeline so the authority is cosmetic.");
    }

    [Test]
    public async Task EmptyAddresses_FallsBackToLocalhost()
    {
        var result = WebSocketHandler.ResolveLoopbackBaseUri(addresses: new List<string>());
        await Assert.That(result).IsEqualTo(TestServerFallback)
            .Because("An empty Addresses collection (TestServer's documented behaviour) should hit the same fallback as null.");
    }

    [Test]
    public async Task SingleHttpListener_LeftAsIs_WhenAlreadySpecific()
    {
        var result = WebSocketHandler.ResolveLoopbackBaseUri(["http://127.0.0.1:5100"]);
        await Assert.That(result).IsEqualTo("http://127.0.0.1:5100")
            .Because("A specific loopback binding has no wildcard to rewrite — the URL is forwarded unchanged.");
    }

    [Test]
    public async Task SingleHttpListener_TrimsTrailingSlash()
    {
        var result = WebSocketHandler.ResolveLoopbackBaseUri(["http://127.0.0.1:5100/"]);
        await Assert.That(result).IsEqualTo("http://127.0.0.1:5100")
            .Because("Trailing slash on the base URL would produce '/api//systems' when concatenated with a leading-slash path.");
    }

    // 0.0.0.0 is Kestrel's shape when bound via ASPNETCORE_HTTP_PORTS; the outbound
    // request cannot dial 0.0.0.0 from inside the same process — rewrite to loopback.
    [Test]
    public async Task WildcardZeroes_AreRewrittenToLoopback()
    {
        var result = WebSocketHandler.ResolveLoopbackBaseUri(["http://0.0.0.0:5100"]);
        await Assert.That(result).IsEqualTo("http://127.0.0.1:5100")
            .Because("0.0.0.0 is a wildcard bind address — calling 0.0.0.0 from inside the same process is not portable; loopback is.");
    }

    // [::] is the IPv6 wildcard Kestrel reports on dual-stack Linux; loopback rewrites
    // to 127.0.0.1 (not [::1]) because the http transport prefers v4 by default.
    [Test]
    public async Task IPv6Wildcard_IsRewrittenToLoopback()
    {
        var result = WebSocketHandler.ResolveLoopbackBaseUri(["http://[::]:5100"]);
        await Assert.That(result).IsEqualTo("http://127.0.0.1:5100")
            .Because("[::] is the IPv6 wildcard — the loopback equivalent for self-calls is 127.0.0.1 (we don't try [::1] because the http transport prefers v4 by default).");
    }

    // Legacy HTTP.sys wildcard shapes; accepted defensively in case an operator's
    // UseUrls("http://+:5100") reintroduces the original bug.
    [Test]
    public async Task PlusWildcard_IsRewrittenToLoopback()
    {
        var result = WebSocketHandler.ResolveLoopbackBaseUri(["http://+:5100"]);
        await Assert.That(result).IsEqualTo("http://127.0.0.1:5100")
            .Because("`+` is the strong-wildcard shape from the HTTP.sys era; covered defensively because UseUrls accepts it.");
    }

    [Test]
    public async Task StarWildcard_IsRewrittenToLoopback()
    {
        var result = WebSocketHandler.ResolveLoopbackBaseUri(["http://*:5100"]);
        await Assert.That(result).IsEqualTo("http://127.0.0.1:5100")
            .Because("`*` is the weak-wildcard shape from the HTTP.sys era; covered defensively because UseUrls accepts it.");
    }

    // Prefer HTTPS to avoid the UseHttpsRedirection 308 round-trip; TLS validation
    // against the local leaf cert is handled by LoopbackHttpClient's permissive validator
    // (safe: dial target is loopback, cannot be MITM'd from outside the process).
    [Test]
    public async Task PrefersHttpsListener_OverHttp_WhenBothPresent()
    {
        var result = WebSocketHandler.ResolveLoopbackBaseUri([
            "http://[::]:5100",
            "https://[::]:5101",
        ]);
        await Assert.That(result).IsEqualTo("https://127.0.0.1:5101")
            .Because("Dialing HTTPS directly avoids the 308 round-trip via UseHttpsRedirection; the loopback HttpClient's permissive validator handles TLS validation.");
    }

    [Test]
    public async Task PrefersHttpsListener_RegardlessOfOrdering()
    {
        var result = WebSocketHandler.ResolveLoopbackBaseUri([
            "https://[::]:5101",
            "http://[::]:5100",
        ]);
        await Assert.That(result).IsEqualTo("https://127.0.0.1:5101")
            .Because("Preference must be based on scheme, not on the order Kestrel emits its bindings.");
    }

    [Test]
    public async Task FallsBackToHttp_WhenOnlyHttpAvailable()
    {
        var result = WebSocketHandler.ResolveLoopbackBaseUri(["http://[::]:5100"]);
        await Assert.That(result).IsEqualTo("http://127.0.0.1:5100")
            .Because("HTTP-only topology is supported — the helper returns a loopback HTTP URL when no HTTPS listener is bound.");
    }

    [Test]
    public async Task SpecificBoundIp_IsLeftUntouched()
    {
        var result = WebSocketHandler.ResolveLoopbackBaseUri(["http://192.168.1.5:5100"]);
        await Assert.That(result).IsEqualTo("http://192.168.1.5:5100")
            .Because("A specific NIC bind address is already reachable as-is — only wildcards (0.0.0.0 / [::] / + / *) get rewritten.");
    }

    [Test]
    public async Task LocalhostBinding_IsLeftUntouched()
    {
        var result = WebSocketHandler.ResolveLoopbackBaseUri(["http://localhost:5100"]);
        await Assert.That(result).IsEqualTo("http://localhost:5100")
            .Because("localhost is already loopback — no wildcard substitution needed.");
    }

    // Original bug: proxy dialed the inbound operator-facing hostname:port. Pin that
    // the helper NEVER returns that shape regardless of input.
    [Test]
    public async Task RegressionGuard_DoesNotReturnOperatorFacingHostname()
    {
        var deploymentTopology = new[]
        {
            "http://0.0.0.0:5100",
            "https://0.0.0.0:5101",
        };
        var result = WebSocketHandler.ResolveLoopbackBaseUri(deploymentTopology);

        using (Assert.Multiple())
        {
            await Assert.That(result).DoesNotContain("pineapple.local")
                .Because("Regression guard: the proxy must never return the inbound request's operator-facing hostname.");
            await Assert.That(result).DoesNotContain(":5001")
                .Because("Regression guard: the proxy must never return the host-side mapped port.");
            await Assert.That(result).IsEqualTo("https://127.0.0.1:5101")
                .Because("Expected the rewritten loopback HTTPS URL targeting the container-internal Kestrel TLS port — preferred over HTTP to avoid the HttpsRedirection 308.");
        }
    }
}
