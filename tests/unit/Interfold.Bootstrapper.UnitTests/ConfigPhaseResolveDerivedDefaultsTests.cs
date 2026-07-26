using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Unit tests for <see cref="ConfigPhase.ResolveDerivedDefaults"/>. The helper is the
/// single source of truth for "what does an empty <see cref="ApiRuntimeSection"/> field
/// resolve to?" — both the interactive form's Show callbacks and the non-interactive
/// <see cref="ConfigPhase.Validate"/> path call it, so its behaviour gets pinned here in
/// isolation.
///
/// Three rules under test:
/// <list type="number">
///   <item>
///     The API-facing fields (<see cref="ApiRuntimeSection.CallbackBaseUrl"/> /
///     <see cref="ApiRuntimeSection.JwtAuthority"/>) always derive with scheme <c>https</c>
///     because the API container's Kestrel default endpoint is bound to the bootstrapper-
///     issued leaf PFX unconditionally (see <c>InterfoldAppHost.ConfigureApiSelfHostEnv</c>),
///     and they carry <c>:{Ports.ApiHttps}</c> unless the operator picked 443.
///     <see cref="DeploymentSection.WebHttps"/> never gates the API-side scheme.
///   </item>
///   <item>
///     The CORS allow-list (<see cref="ApiRuntimeSection.CorsAllowedOrigins"/>) represents
///     the SPA / native-client origins. Its scheme follows
///     <see cref="DeploymentSection.WebHttps"/> and its port follows
///     <see cref="PortsSection.WebHttps"/> or <see cref="PortsSection.WebHttp"/>, with the
///     port suffix dropped when the operator picked the scheme's default port (80 for http,
///     443 for https). The primary host (first non-CIDR
///     <see cref="DeploymentSection.Hosts"/> entry) wins for the two single-value API fields;
///     CORS gets one entry per non-CIDR host. IPv6 literals are bracket-wrapped per
///     RFC 3986 §3.2.2.
///   </item>
///   <item>A non-empty stored value always wins over the derived default — derivation only
///         fills blanks, it never overwrites.</item>
/// </list>
/// </summary>
public sealed class ConfigPhaseResolveDerivedDefaultsTests
{
    [Test]
    public async Task ApiUrlIsHttpsWithApiHttpsPortRegardlessOfWebHttpsFalse()
    {
        // The API container always terminates HTTPS in self-host — its Kestrel default
        // endpoint binds to the bootstrapper-issued leaf PFX unconditionally, independent of
        // Deployment.WebHttps (which only governs the web container). So the derived
        // CallbackBaseUrl and JwtAuthority must say `https` and carry `:{Ports.ApiHttps}`
        // even when the operator turned the web tier's HTTPS off.
        var cfg = TestSupport.MakeConfigWithEmptyApiRuntime(webHttps: false, hosts: "api.example.com");

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.ApiRuntime.CallbackBaseUrl).IsEqualTo("https://api.example.com:5001");
        await Assert.That(cfg.ApiRuntime.JwtAuthority).IsEqualTo("https://api.example.com:5001");
        // CORS still tracks the web tier — http on default Ports.WebHttp=8080 here.
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins).Contains("http://api.example.com:8080");
    }

    [Test]
    public async Task ApiUrlStaysHttpsWithApiHttpsPortWhenWebHttpsTrue()
    {
        var cfg = TestSupport.MakeConfigWithEmptyApiRuntime(webHttps: true, hosts: "api.example.com");

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.ApiRuntime.CallbackBaseUrl).IsEqualTo("https://api.example.com:5001");
        await Assert.That(cfg.ApiRuntime.JwtAuthority).IsEqualTo("https://api.example.com:5001");
        // CORS scheme flips to https alongside WebHttps; port becomes Ports.WebHttps=8081.
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins).Contains("https://api.example.com:8081");
    }

    [Test]
    public async Task ApiHttpsPort443OmitsPortSuffix()
    {
        // Operator fronting the API with a 443-bound proxy (or rebinding Kestrel onto 443
        // directly) gets a clean URL without a port suffix — matches the canonical form for
        // an https URI on its default port (RFC 7230 §2.7.2).
        var cfg = TestSupport.MakeConfigWithEmptyApiRuntime(webHttps: true, hosts: "api.example.com");
        cfg.Ports.ApiHttps = 443;

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.ApiRuntime.CallbackBaseUrl).IsEqualTo("https://api.example.com");
        await Assert.That(cfg.ApiRuntime.JwtAuthority).IsEqualTo("https://api.example.com");
    }

    [Test]
    public async Task CorsOmitsPortSuffixOnDefaultHttpsPort()
    {
        var cfg = TestSupport.MakeConfigWithEmptyApiRuntime(webHttps: true, hosts: "api.example.com");
        cfg.Ports.WebHttps = 443;

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins).Contains("https://api.example.com");
    }

    [Test]
    public async Task CorsOmitsPortSuffixOnDefaultHttpPort()
    {
        var cfg = TestSupport.MakeConfigWithEmptyApiRuntime(webHttps: false, hosts: "api.example.com");
        cfg.Ports.WebHttp = 80;

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins).Contains("http://api.example.com");
    }

    [Test]
    public async Task PrimaryHostWinsForSingleValueFields()
    {
        // Two hosts supplied. CallbackBaseUrl and JwtAuthority must take only the FIRST
        // non-CIDR one — they're single-value scalars, and the bootstrapper's contract says
        // the operator's primary public host is what callbacks and JWT iss claims target.
        var cfg = TestSupport.MakeConfigWithEmptyApiRuntime(
            webHttps: true,
            "api.example.com", "admin.example.com");

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.ApiRuntime.CallbackBaseUrl).IsEqualTo("https://api.example.com:5001");
        await Assert.That(cfg.ApiRuntime.JwtAuthority).IsEqualTo("https://api.example.com:5001");
        // …but the second host still ends up in the CORS list (with the web port suffix).
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins).Contains("https://api.example.com:8081");
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins).Contains("https://admin.example.com:8081");
    }

    [Test]
    public async Task CorsListGetsOneEntryPerHost()
    {
        // CORS preserves all non-CIDR hosts: every leaf-eligible entry on the deployment side
        // gets a matching entry in the allow-list, in the same order, with the scheme + port
        // derived from WebHttps + the matching Ports.Web* slot.
        var cfg = TestSupport.MakeConfigWithEmptyApiRuntime(
            webHttps: false,
            "api.example.com", "admin.example.com", "www.example.com");

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins.Count).IsEqualTo(3);
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins[0]).IsEqualTo("http://api.example.com:8080");
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins[1]).IsEqualTo("http://admin.example.com:8080");
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins[2]).IsEqualTo("http://www.example.com:8080");
    }

    [Test]
    public async Task StoredCallbackBaseUrlWinsOverDerived()
    {
        // The "stored wins" rule applied to CallbackBaseUrl: if the operator supplied an
        // explicit value (interactive or via the JSON), derivation must not overwrite it
        // — even if the value differs from what derivation would produce.
        var cfg = TestSupport.MakeConfigWithEmptyApiRuntime(webHttps: true, hosts: "api.example.com");
        cfg.ApiRuntime.CallbackBaseUrl = "https://callback-override.example.com";

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.ApiRuntime.CallbackBaseUrl).IsEqualTo("https://callback-override.example.com");
        // JwtAuthority was empty so it still derives.
        await Assert.That(cfg.ApiRuntime.JwtAuthority).IsEqualTo("https://api.example.com:5001");
    }

    [Test]
    public async Task StoredJwtAuthorityWinsOverDerived()
    {
        var cfg = TestSupport.MakeConfigWithEmptyApiRuntime(webHttps: true, hosts: "api.example.com");
        cfg.ApiRuntime.JwtAuthority = "https://issuer-override.example.com";

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.ApiRuntime.JwtAuthority).IsEqualTo("https://issuer-override.example.com");
        await Assert.That(cfg.ApiRuntime.CallbackBaseUrl).IsEqualTo("https://api.example.com:5001");
    }

    [Test]
    public async Task StoredCorsListWinsOverDerived()
    {
        // A non-empty CORS list (even with a single entry) must survive derivation untouched.
        var cfg = TestSupport.MakeConfigWithEmptyApiRuntime(webHttps: true, hosts: "api.example.com");
        cfg.ApiRuntime.CorsAllowedOrigins = ["https://custom-front.example.com"];

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins.Count).IsEqualTo(1);
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins[0]).IsEqualTo("https://custom-front.example.com");
    }

    [Test]
    public async Task IsIdempotent()
    {
        // Calling ResolveDerivedDefaults twice must produce the same end state as calling it
        // once — once a value is filled, the second pass sees it as non-empty and skips it.
        // The interactive form's Show callbacks fire on every redraw, so this property is
        // load-bearing for menu stability.
        var cfg = TestSupport.MakeConfigWithEmptyApiRuntime(webHttps: true, hosts: "api.example.com");

        ConfigPhase.ResolveDerivedDefaults(cfg);
        var firstCallback = cfg.ApiRuntime.CallbackBaseUrl;
        var firstAuthority = cfg.ApiRuntime.JwtAuthority;
        var firstCorsCount = cfg.ApiRuntime.CorsAllowedOrigins.Count;

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.ApiRuntime.CallbackBaseUrl).IsEqualTo(firstCallback);
        await Assert.That(cfg.ApiRuntime.JwtAuthority).IsEqualTo(firstAuthority);
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins.Count).IsEqualTo(firstCorsCount);
    }

    [Test]
    public async Task EmptyHostsLeavesEverythingUnset()
    {
        // ResolveDerivedDefaults has nothing to work from when Hosts is empty. Validate's
        // host check will throw downstream, but ResolveDerivedDefaults itself must early-out
        // gracefully rather than throwing or writing junk values.
        var cfg = TestSupport.MakeConfigWithEmptyApiRuntime();
        cfg.Deployment.Hosts = [];

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.ApiRuntime.CallbackBaseUrl).IsEqualTo(string.Empty);
        await Assert.That(cfg.ApiRuntime.JwtAuthority).IsEqualTo(string.Empty);
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DoesNotOverwriteJwtAudience()
    {
        // JwtAudience has a hardcoded property-initialiser default ("octocon") and is NOT a
        // derivable field — derivation must leave it alone in every path.
        var cfg = TestSupport.MakeConfigWithEmptyApiRuntime(webHttps: true, hosts: "api.example.com");
        cfg.ApiRuntime.JwtAudience = "custom-aud";

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.ApiRuntime.JwtAudience).IsEqualTo("custom-aud");
    }

    [Test]
    public async Task Ipv6PrimaryHostProducesBracketedUrl()
    {
        // RFC 3986 §3.2.2: a URI authority containing an IPv6 literal MUST bracket-wrap the
        // literal so the colons aren't interpreted as port separators. Pins the
        // HostParser.ToUrlHost behaviour as observed through the derivation surface that real
        // consumers (callback URL, JWT issuer) see — the port suffix still appears after the
        // closing bracket on a non-default port.
        var cfg = TestSupport.MakeConfigWithEmptyApiRuntime(webHttps: false, hosts: "::1");

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.ApiRuntime.CallbackBaseUrl).IsEqualTo("https://[::1]:5001");
        await Assert.That(cfg.ApiRuntime.JwtAuthority).IsEqualTo("https://[::1]:5001");
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins).Contains("http://[::1]:8080");
    }

    [Test]
    public async Task CidrEntryIgnoredForUrlDerivation()
    {
        // CIDR entries widen the Name Constraints scope but have no URL form, so they must be
        // skipped when picking the primary host and when building the CORS allow-list.
        // ["192.168.1.0/24", "192.168.1.42"] → primary is 192.168.1.42; CORS has exactly one
        // entry (the CIDR doesn't add a second).
        var cfg = TestSupport.MakeConfigWithEmptyApiRuntime(webHttps: false, hosts: ["192.168.1.0/24", "192.168.1.42"]);

        ConfigPhase.ResolveDerivedDefaults(cfg);

        await Assert.That(cfg.ApiRuntime.CallbackBaseUrl).IsEqualTo("https://192.168.1.42:5001");
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins.Count).IsEqualTo(1);
        await Assert.That(cfg.ApiRuntime.CorsAllowedOrigins[0]).IsEqualTo("http://192.168.1.42:8080");
    }
}

