using System.Net;
using System.Text.RegularExpressions;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using Interfold.Contracts.Enums;
using Spectre.Console;
using Spectre.Console.Testing;
using TUnit.Core;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Drives <see cref="ConfigPhase.PromptForConfig(IAnsiConsole, bool, Func{IPAddress?}?, Func{string?}?)"/>
/// through an in-memory <see cref="TestConsole"/> to exercise the Spectre navigable form.
/// <para>
/// The form is a single <c>SelectionPrompt&lt;int&gt;</c> with 48 selectable field rows
/// under 11 section headers + a trailing <c>Confirm and save</c>. Headers are inert; the
/// cursor starts at field 0 and returns there after every edit, so tests use absolute
/// navigation distances (see <see cref="Navigate"/> / <see cref="EditField"/>).
/// </para>
/// <para>
/// Field order (0-based DownArrow distance from field 0):
/// 0..6 Deployment · 7..12 Ports · 13..16 Database · 17..21 API ·
/// 22..23 Cluster &amp; telemetry · 24..25 Storage · 26..30 Performance tuning ·
/// 31..36 OAuth credentials · 37..41 Backup &amp; autostart · 42..46 Updates · 47 Firebase.
/// <see cref="Navigate"/>(48) lands on <c>Confirm and save</c>.
/// </para>
/// </summary>
public sealed class ConfigInteractivePromptTests
{
    /// <summary>Selectable field-row count.</summary>
    private const int FieldCount = 48;

    /// <summary>
    /// Interactive <see cref="TestConsole"/> sized to fit the whole 60-row form so Spectre
    /// never paginates it (pagination clips top rows out of the captured output).
    /// </summary>
    private static TestConsole NewConsole()
    {
        var c = new TestConsole();
        c.Interactive();
        c.Profile.Height = 120;
        c.Profile.Width = 130;
        return c;
    }

    /// <summary>
    /// Pushes <paramref name="downArrows"/> DownArrow presses + Enter. Field index N sits N
    /// DownArrows below the cursor's starting position (field 0).
    /// </summary>
    private static void Navigate(TestConsole c, int downArrows)
    {
        for (var i = 0; i < downArrows; i++)
            c.Input.PushKey(ConsoleKey.DownArrow);
        c.Input.PushKey(ConsoleKey.Enter);
    }

    /// <summary>Selects <c>Confirm and save</c> from the form's default starting position.</summary>
    private static void ConfirmForm(TestConsole c) => Navigate(c, FieldCount);

    /// <summary>
    /// Opens the field's editor and pushes <paramref name="answers"/> as answer lines. Pass
    /// multiple answers to drive a re-prompt on invalid input.
    /// </summary>
    private static void EditField(TestConsole c, int fieldIndex, params string[] answers)
    {
        Navigate(c, fieldIndex);
        foreach (var a in answers)
            c.Input.PushTextWithEnter(a);
    }

    /// <summary>
    /// Standard wrapper — both probes return null so neither auto-default fires in tests. A
    /// CI runner with a routable NIC would otherwise pre-populate Hosts and break the empty-
    /// Hosts assertions. Tests exercising the auto-default call <c>PromptForConfig</c> directly.
    /// </summary>
    private static BootstrapConfig PromptWithoutDetection(IAnsiConsole console, bool maskSecrets = false) =>
        ConfigPhase.PromptForConfig(
            console,
            maskSecrets,
            localAddressProbe: () => null,
            hostnameProbe: () => null);

    [Test]
    public async Task ConfirmingFormImmediatelyUsesDefaults()
    {
        var console = NewConsole();
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        // Deployment defaults
        await Assert.That(config.Deployment.OutputDir).IsEqualTo("./deploy");
        await Assert.That(config.Deployment.RootCaName).IsEqualTo("Interfold Root CA");
        await Assert.That(config.Deployment.CertYears).IsEqualTo(5);
        await Assert.That(config.Deployment.TrustStoreInstall).IsTrue();
        await Assert.That(config.Deployment.IncludeWeb).IsFalse();
        await Assert.That(config.Deployment.WebHttps).IsFalse();
        // No default; the auto-fill is suppressed via PromptWithoutDetection. Validate
        // fails fast on this state downstream (see DeploymentSection.Hosts).
        await Assert.That(config.Deployment.Hosts.Count).IsEqualTo(0);

        // Ports defaults
        await Assert.That(config.Ports.ApiHttp).IsEqualTo(5000);
        await Assert.That(config.Ports.ApiHttps).IsEqualTo(5001);
        await Assert.That(config.Ports.WebHttp).IsEqualTo(8080);
        await Assert.That(config.Ports.WebHttps).IsEqualTo(8081);
        await Assert.That(config.Ports.Postgres).IsEqualTo(4200);
        await Assert.That(config.Ports.Scylla).IsEqualTo(9042);

        // Database / API defaults
        await Assert.That(config.DatabaseMode).IsEqualTo(DatabaseMode.Single);
        await Assert.That(config.PostgresDatabase).IsEqualTo("interfold");
        await Assert.That(config.ClusterName).IsEqualTo("InterfoldCluster");
        await Assert.That(config.ScyllaKeyspace).IsEqualTo(ScyllaKeyspace.Nam);
        await Assert.That(config.ApiImage).IsEqualTo("ghcr.io/azyyyyyy/interfold-api:latest");

        // ApiRuntime: derivation happens in RunAsync / Validate, not PromptForConfig, so
        // the stored fields stay empty here (the menu's Show callbacks derive for display only).
        await Assert.That(config.ApiRuntime.CallbackBaseUrl).IsEqualTo(string.Empty);
        await Assert.That(config.ApiRuntime.JwtAuthority).IsEqualTo(string.Empty);
        await Assert.That(config.ApiRuntime.JwtAudience).IsEqualTo("octocon");
        await Assert.That(config.ApiRuntime.CorsAllowedOrigins.Count).IsEqualTo(0);

        // Blank string/null fields reproduce the pre-bootstrapper "env var unset" behaviour.
        await Assert.That(config.Cluster.NodeGroup).IsEqualTo(NodeGroup.Auxiliary);
        await Assert.That(config.Observability.OtlpEndpoint).IsEqualTo(string.Empty);
        await Assert.That(config.Storage.AvatarStorageRoot).IsEqualTo(string.Empty);
        await Assert.That(config.Storage.AvatarPublicBase).IsEqualTo(string.Empty);
        await Assert.That(config.Socket.BatchBytesThreshold).IsNull();
        await Assert.That(config.Persistence.DbRetryAttempts).IsEqualTo(3);
        await Assert.That(config.Persistence.DbRetryInitialDelayMs).IsEqualTo(100);
        await Assert.That(config.Persistence.DbRetryMaxDelayMs).IsEqualTo(1500);
        await Assert.That(config.Persistence.HydrationMaxConcurrency).IsEqualTo(8);

        // OAuth credentials default to empty (paired per provider: ID then secret).
        await Assert.That(config.OAuth.GoogleClientId).IsEqualTo(string.Empty);
        await Assert.That(config.OAuth.GoogleClientSecret).IsEqualTo(string.Empty);
        await Assert.That(config.OAuth.DiscordClientId).IsEqualTo(string.Empty);
        await Assert.That(config.OAuth.DiscordClientSecret).IsEqualTo(string.Empty);
        await Assert.That(config.OAuth.AppleClientId).IsEqualTo(string.Empty);
        await Assert.That(config.OAuth.AppleClientSecret).IsEqualTo(string.Empty);

        // Update section defaults to "manual only" — stock bootstrap matches pre-feature deployments.
        await Assert.That(config.Update.Enabled).IsFalse();
        await Assert.That(config.Update.HealthCheckTimeoutSeconds).IsEqualTo(180);
        await Assert.That(config.Update.AutoRestoreOnFailure).IsFalse();
        await Assert.That(config.Update.RecreateOnUpdate).IsTrue();
        await Assert.That(config.Update.Services.Length).IsEqualTo(0);
    }

    [Test]
    public async Task EditingHostsParsesCommaSeparated()
    {
        var console = NewConsole();
        EditField(console, fieldIndex: 1, "api.example.com,admin.example.com,www.example.com");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Deployment.Hosts.Count).IsEqualTo(3);
        await Assert.That(config.Deployment.Hosts).Contains("api.example.com");
        await Assert.That(config.Deployment.Hosts).Contains("admin.example.com");
        await Assert.That(config.Deployment.Hosts).Contains("www.example.com");
    }

    [Test]
    public async Task EditingHostsTrimsWhitespace()
    {
        var console = NewConsole();
        EditField(console, fieldIndex: 1, "  foo.example.com  ,  bar.example.com  ");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Deployment.Hosts.Count).IsEqualTo(2);
        await Assert.That(config.Deployment.Hosts).Contains("foo.example.com");
        await Assert.That(config.Deployment.Hosts).Contains("bar.example.com");
    }

    [Test]
    public async Task EditingHostsAcceptsIpAndCidr()
    {
        // IPv4 literal + IPv6 CIDR must reach the stored list verbatim.
        var console = NewConsole();
        EditField(console, fieldIndex: 1, "192.168.1.42,fe80::/64");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Deployment.Hosts.Count).IsEqualTo(2);
        await Assert.That(config.Deployment.Hosts[0]).IsEqualTo("192.168.1.42");
        await Assert.That(config.Deployment.Hosts[1]).IsEqualTo("fe80::/64");
    }

    [Test]
    public async Task DetectedDeviceIpPreFillsHostsOnFreshBootstrap()
    {
        // Deterministic probe (test doesn't depend on the runner's NICs).
        var console = NewConsole();
        ConfirmForm(console);

        var config = ConfigPhase.PromptForConfig(
            console,
            maskSecrets: false,
            localAddressProbe: () => IPAddress.Parse("192.168.1.42"));

        await Assert.That(config.Deployment.Hosts.Count).IsEqualTo(1);
        await Assert.That(config.Deployment.Hosts[0]).IsEqualTo("192.168.1.42");
    }

    [Test]
    public async Task DetectedIpv6IsStoredAsBareLiteralNotBracketed()
    {
        // Downstream consumers (SAN encoder, URL derivation) expect the bare literal;
        // storing "[::1]" would round-trip into the JSON with brackets and surprise editors.
        var console = NewConsole();
        ConfirmForm(console);

        var config = ConfigPhase.PromptForConfig(
            console,
            maskSecrets: false,
            localAddressProbe: () => IPAddress.Parse("2001:db8::1"));

        await Assert.That(config.Deployment.Hosts.Count).IsEqualTo(1);
        await Assert.That(config.Deployment.Hosts[0]).IsEqualTo("2001:db8::1");
    }

    [Test]
    public async Task OperatorEditedHostsOverrideDetectedIpDefault()
    {
        // Auto-default is just a pre-fill — a typed value must win.
        var console = NewConsole();
        EditField(console, fieldIndex: 1, "api.example.com");
        ConfirmForm(console);

        var config = ConfigPhase.PromptForConfig(
            console,
            maskSecrets: false,
            localAddressProbe: () => IPAddress.Parse("192.168.1.42"));

        await Assert.That(config.Deployment.Hosts.Count).IsEqualTo(1);
        await Assert.That(config.Deployment.Hosts[0]).IsEqualTo("api.example.com");
    }

    [Test]
    public async Task NullDetectorLeavesHostsEmpty()
    {
        // Null detector must NOT invent a fallback (would mask a future "default to 127.0.0.1"
        // regression). Validate fails fast on the resulting empty Hosts downstream.
        var console = NewConsole();
        ConfirmForm(console);

        var config = ConfigPhase.PromptForConfig(
            console,
            maskSecrets: false,
            localAddressProbe: () => null);

        await Assert.That(config.Deployment.Hosts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DetectedIpAppearsInPublicHostMenuRow()
    {
        // Guards against a refactor that pre-fills Hosts but doesn't render the value on its row.
        var console = NewConsole();
        ConfirmForm(console);

        ConfigPhase.PromptForConfig(
            console,
            maskSecrets: false,
            localAddressProbe: () => IPAddress.Parse("10.0.0.42"));

        await Assert.That(Regex.IsMatch(console.Output, @"Public host\(s\)\s+10\.0\.0\.42")).IsTrue();
    }

    [Test]
    public async Task HostsPrefillIncludesMdnsHostnameAndIp()
    {
        // Order matters: hostname first so HostParser.PickPrimary latches it for leaf-cert
        // and derived-URL purposes. A swap would silently point every derived URL at the IP.
        var console = NewConsole();
        ConfirmForm(console);

        var config = ConfigPhase.PromptForConfig(
            console,
            maskSecrets: false,
            localAddressProbe: () => IPAddress.Parse("192.168.1.42"),
            hostnameProbe: () => "workstation.local");

        await Assert.That(config.Deployment.Hosts.Count).IsEqualTo(2);
        await Assert.That(config.Deployment.Hosts[0]).IsEqualTo("workstation.local");
        await Assert.That(config.Deployment.Hosts[1]).IsEqualTo("192.168.1.42");
        // Menu row = comma-joined; pin the exact display so a "print only Hosts[0]" regression gets caught.
        await Assert.That(Regex.IsMatch(console.Output, @"Public host\(s\)\s+workstation\.local,192\.168\.1\.42")).IsTrue();
    }

    [Test]
    public async Task HostsPrefillOmitsHostnameWhenProbeReturnsNull()
    {
        // Null hostname probe (banner reported mDNS unavailable) must not become an empty
        // or null entry in Hosts — only the detected IP lands.
        var console = NewConsole();
        ConfirmForm(console);

        var config = ConfigPhase.PromptForConfig(
            console,
            maskSecrets: false,
            localAddressProbe: () => IPAddress.Parse("192.168.1.42"),
            hostnameProbe: () => null);

        await Assert.That(config.Deployment.Hosts.Count).IsEqualTo(1);
        await Assert.That(config.Deployment.Hosts[0]).IsEqualTo("192.168.1.42");
    }

    [Test]
    public async Task EditingOAuthSecretsCapturesValues()
    {
        // Paired per provider (ID then secret): secrets sit at 32 / 34 / 36.
        var console = NewConsole();
        EditField(console, fieldIndex: 32, "google-secret-xyz");
        EditField(console, fieldIndex: 34, "discord-secret-abc");
        EditField(console, fieldIndex: 36, "apple-secret-jwt");
        ConfirmForm(console);

        // maskSecrets:false keeps the prompt off the ReadKey path so PushTextWithEnter suffices.
        var config = PromptWithoutDetection(console);

        await Assert.That(config.OAuth.GoogleClientSecret).IsEqualTo("google-secret-xyz");
        await Assert.That(config.OAuth.DiscordClientSecret).IsEqualTo("discord-secret-abc");
        await Assert.That(config.OAuth.AppleClientSecret).IsEqualTo("apple-secret-jwt");
    }

    [Test]
    public async Task EditingOAuthClientIdsCapturesValues()
    {
        // Public IDs → plain PromptStr (no masking). ID rows sit at 31 / 33 / 35.
        var console = NewConsole();
        EditField(console, fieldIndex: 31, "1234.apps.googleusercontent.com");
        EditField(console, fieldIndex: 33, "9876543210");
        EditField(console, fieldIndex: 35, "com.example.interfold.signin");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.OAuth.GoogleClientId).IsEqualTo("1234.apps.googleusercontent.com");
        await Assert.That(config.OAuth.DiscordClientId).IsEqualTo("9876543210");
        await Assert.That(config.OAuth.AppleClientId).IsEqualTo("com.example.interfold.signin");
    }

    [Test]
    public async Task FormShowsEmptyMarkerNextToUnsetOAuthClientIds()
    {
        // Parity with secret rows: an unset ID must render <empty>, not a blank cell (which
        // reads as "no row" and can lead operators to miss the second half of a provider's pair).
        var console = NewConsole();
        ConfirmForm(console);

        PromptWithoutDetection(console);

        var output = console.Output;
        await Assert.That(Regex.IsMatch(output, @"Google OAuth client ID\s+<empty>")).IsTrue();
        await Assert.That(Regex.IsMatch(output, @"Discord OAuth client ID\s+<empty>")).IsTrue();
        await Assert.That(Regex.IsMatch(output, @"Apple OAuth client ID\s+<empty>")).IsTrue();
    }

    [Test]
    public async Task FormShowsOAuthClientIdsVerbatimInMenuRow()
    {
        // IDs are public → the menu row echoes the value verbatim, not <set>/<empty>.
        const string googleId = "1234.apps.googleusercontent.com";
        var console = NewConsole();
        EditField(console, fieldIndex: 31, googleId);
        ConfirmForm(console);

        PromptWithoutDetection(console);

        await Assert.That(console.Output).Contains(googleId);
    }

    [Test]
    public async Task EditingPortsCapturesOverrides()
    {
        var console = NewConsole();
        EditField(console, fieldIndex: 7,  "5100");   // ApiHttp
        EditField(console, fieldIndex: 8,  "5101");   // ApiHttps
        EditField(console, fieldIndex: 9,  "8090");   // WebHttp
        EditField(console, fieldIndex: 10,  "8091");   // WebHttps
        EditField(console, fieldIndex: 11, "4300");   // Postgres
        EditField(console, fieldIndex: 12, "9043");   // Scylla
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Ports.ApiHttp).IsEqualTo(5100);
        await Assert.That(config.Ports.ApiHttps).IsEqualTo(5101);
        await Assert.That(config.Ports.WebHttp).IsEqualTo(8090);
        await Assert.That(config.Ports.WebHttps).IsEqualTo(8091);
        await Assert.That(config.Ports.Postgres).IsEqualTo(4300);
        await Assert.That(config.Ports.Scylla).IsEqualTo(9043);
    }

    [Test]
    public async Task EditingBoolsCapturesOverrides()
    {
        var console = NewConsole();
        EditField(console, fieldIndex: 4, "n");   // TrustStoreInstall := false
        EditField(console, fieldIndex: 6, "y");   // WebHttps := true
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Deployment.TrustStoreInstall).IsFalse();
        await Assert.That(config.Deployment.WebHttps).IsTrue();
    }

    [Test]
    public async Task EditingIncludeWebCapturesOverride()
    {
        // IncludeWeb (row 5) is independent from WebHttps (row 6) — toggling one must not
        // silently promote the other.
        var console = NewConsole();
        EditField(console, fieldIndex: 5, "y");   // IncludeWeb := true
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Deployment.IncludeWeb).IsTrue();
        await Assert.That(config.Deployment.WebHttps).IsFalse();
    }

    [Test]
    public async Task PortPromptRePromptsOnInvalidInt()
    {
        // "bad" triggers the re-prompt; the second answer (5005) is what should stick.
        var console = NewConsole();
        EditField(console, fieldIndex: 7, "bad", "5005");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Ports.ApiHttp).IsEqualTo(5005);
        await Assert.That(console.Output).Contains("must be an integer");
    }

    [Test]
    public async Task BoolPromptRePromptsOnInvalidAnswer()
    {
        var console = NewConsole();
        EditField(console, fieldIndex: 4, "maybe", "n");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Deployment.TrustStoreInstall).IsFalse();
    }

    [Test]
    public async Task DatabaseModePromptEnforcesChoices()
    {
        var console = NewConsole();
        EditField(console, fieldIndex: 13, "invalid", "multi");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.DatabaseMode).IsEqualTo(DatabaseMode.Multi);
    }

    [Test]
    public async Task EditingScyllaKeyspaceCapturesValue()
    {
        // Happy-path edit; AddChoices enforcement is covered by the rejection test below.
        var console = NewConsole();
        EditField(console, fieldIndex: 16, "eur");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.ScyllaKeyspace).IsEqualTo(ScyllaKeyspace.Eur);
    }

    [Test]
    public async Task ScyllaKeyspacePromptEnforcesChoices()
    {
        // AddChoices re-prompts on non-listed values; the eventually-accepted value sticks.
        var console = NewConsole();
        EditField(console, fieldIndex: 16, "ant", "gdpr");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.ScyllaKeyspace).IsEqualTo(ScyllaKeyspace.Gdpr);
    }

    [Test]
    public async Task EditingApiRuntimeCapturesValues()
    {
        // Rows 17-20: CallbackBaseUrl / JwtAuthority / JwtAudience / CORS. Verbatim round-trip.
        var console = NewConsole();
        EditField(console, fieldIndex: 17, "https://api.custom.example.com");
        EditField(console, fieldIndex: 18, "https://issuer.custom.example.com");
        EditField(console, fieldIndex: 19, "custom-aud");
        EditField(console, fieldIndex: 20, "https://app.example.com,https://admin.example.com");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.ApiRuntime.CallbackBaseUrl).IsEqualTo("https://api.custom.example.com");
        await Assert.That(config.ApiRuntime.JwtAuthority).IsEqualTo("https://issuer.custom.example.com");
        await Assert.That(config.ApiRuntime.JwtAudience).IsEqualTo("custom-aud");
        await Assert.That(config.ApiRuntime.CorsAllowedOrigins.Count).IsEqualTo(2);
        await Assert.That(config.ApiRuntime.CorsAllowedOrigins).Contains("https://app.example.com");
        await Assert.That(config.ApiRuntime.CorsAllowedOrigins).Contains("https://admin.example.com");
    }

    [Test]
    public async Task FormShowsDerivedCallbackBaseUrlInMenuRow()
    {
        // ApiRuntime Show callbacks paint the derived default when the stored field is empty.
        // With WebHttps=false and shipped port defaults: callback is https://host:5001 (API
        // always https in self-host), CORS is http://host:8080 (web tier).
        var console = NewConsole();
        EditField(console, fieldIndex: 1, "api.example.com");
        ConfirmForm(console);

        PromptWithoutDetection(console);

        var output = console.Output;
        await Assert.That(Regex.IsMatch(output, @"OAuth callback base URL\s+https://api\.example\.com:5001")).IsTrue();
        await Assert.That(Regex.IsMatch(output, @"JWT authority \(iss claim\)\s+https://api\.example\.com:5001")).IsTrue();
        await Assert.That(Regex.IsMatch(output, @"CORS allowed origins\s+http://api\.example\.com:8080")).IsTrue();
    }

    [Test]
    public async Task CorsAllowedOriginsRePromptsOnInvalidUri()
    {
        // Bare hostnames aren't absolute http(s) URIs — re-prompt; second answer sticks.
        var console = NewConsole();
        EditField(console, fieldIndex: 20,
            "not-a-url,still-not-a-url",
            "https://app.example.com,https://admin.example.com");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.ApiRuntime.CorsAllowedOrigins.Count).IsEqualTo(2);
        await Assert.That(config.ApiRuntime.CorsAllowedOrigins).Contains("https://app.example.com");
        await Assert.That(console.Output).Contains("not a valid http(s) origin");
    }

    [Test]
    public async Task FormListsEveryFieldLabel()
    {
        // Confirming immediately is enough — the menu renders before the first key is consumed.
        var console = NewConsole();
        ConfirmForm(console);

        PromptWithoutDetection(console);

        var output = console.Output;
        // Deployment
        await Assert.That(output).Contains("Output directory");
        await Assert.That(output).Contains("Public host");
        await Assert.That(output).Contains("Root CA");
        await Assert.That(output).Contains("Leaf cert validity");
        await Assert.That(output).Contains("Install root CA");
        await Assert.That(output).Contains("octocon-web");
        // Ports
        await Assert.That(output).Contains("API HTTP port");
        await Assert.That(output).Contains("API HTTPS port");
        await Assert.That(output).Contains("Web HTTP port");
        await Assert.That(output).Contains("Web HTTPS port");
        await Assert.That(output).Contains("Postgres host port");
        await Assert.That(output).Contains("Scylla");
        // Database
        await Assert.That(output).Contains("Database mode");
        await Assert.That(output).Contains("Postgres application DB name");
        await Assert.That(output).Contains("Cluster name");
        await Assert.That(output).Contains("Scylla keyspace (region)");
        // API
        await Assert.That(output).Contains("OAuth callback base URL");
        await Assert.That(output).Contains("JWT authority (iss claim)");
        await Assert.That(output).Contains("JWT audience (aud claim)");
        await Assert.That(output).Contains("CORS allowed origins");
        await Assert.That(output).Contains("API image");
        // Cluster & telemetry
        await Assert.That(output).Contains("Cluster node group");
        await Assert.That(output).Contains("OTLP endpoint");
        // Storage
        await Assert.That(output).Contains("Avatar storage root");
        await Assert.That(output).Contains("Avatar public base URL");
        // Performance tuning
        await Assert.That(output).Contains("Socket batch flush threshold");
        await Assert.That(output).Contains("DB retry attempts");
        await Assert.That(output).Contains("DB retry initial delay");
        await Assert.That(output).Contains("DB retry max delay");
        await Assert.That(output).Contains("Hydration max concurrency");
        // OAuth: assert ID and secret rows separately so a dropped label is caught.
        await Assert.That(output).Contains("Google OAuth client ID");
        await Assert.That(output).Contains("Google OAuth client secret");
        await Assert.That(output).Contains("Discord OAuth client ID");
        await Assert.That(output).Contains("Discord OAuth client secret");
        await Assert.That(output).Contains("Apple OAuth client ID");
        await Assert.That(output).Contains("Apple OAuth client secret");
    }

    [Test]
    public async Task FormListsEverySectionHeader()
    {
        var console = NewConsole();
        ConfirmForm(console);

        PromptWithoutDetection(console);

        var output = console.Output;
        await Assert.That(output).Contains("Deployment");
        await Assert.That(output).Contains("Ports");
        await Assert.That(output).Contains("Database");
        // "---" framing distinguishes ambiguous tokens (e.g. "API" also appears in field labels).
        await Assert.That(Regex.IsMatch(output, @"---\s+API\s+---")).IsTrue();
        await Assert.That(output).Contains("Cluster & telemetry");
        await Assert.That(Regex.IsMatch(output, @"---\s+Storage\s+---")).IsTrue();
        await Assert.That(output).Contains("Performance tuning");
        await Assert.That(output).Contains("OAuth credentials");
        await Assert.That(output).Contains("Backup & autostart");
        await Assert.That(Regex.IsMatch(output, @"---\s+Updates\s+---")).IsTrue();
        await Assert.That(output).Contains("Configure interfold.bootstrap.json");
        await Assert.That(output).Contains("Confirm and save");
    }

    [Test]
    public async Task SectionHeadersSitAboveTheirFirstField()
    {
        // Position guard (FormListsEverySectionHeader only checks presence). The grouped
        // declaration makes mechanical index drift impossible, so this now catches a field
        // declared under the wrong Group(...) — same visible symptom.
        // Assert: previous section's last field < header < current section's first field.
        var console = NewConsole();
        ConfirmForm(console);

        PromptWithoutDetection(console);

        var output = console.Output;
        // (previous section's last field label, header text, section's first field label)
        var boundaries = new (string PrevField, string Header, string FirstField)[]
        {
            ("Terminate HTTPS at octocon-web",           "--- Ports ---",              "API HTTP port"),
            ("Scylla/Cassandra host port",               "--- Database ---",           "Database mode"),
            ("Scylla keyspace (region)",                 "--- API ---",                "OAuth callback base URL"),
            ("Pre-built Interfold API image",            "--- Cluster & telemetry ---", "Cluster node group"),
            ("OTLP endpoint",                            "--- Storage ---",            "Avatar storage root"),
            ("Avatar public base URL",                   "--- Performance tuning ---", "Socket batch flush threshold"),
            ("Hydration max concurrency",                "--- OAuth credentials ---",  "Google OAuth client ID"),
            ("Apple OAuth client secret",                "--- Backup & autostart ---", "Scheduled backups enabled"),
            ("Autostart server on boot",                 "--- Updates ---",            "Chain updates after backup"),
            ("Update service whitelist",                 "--- Firebase ---",           "Firebase push notifications"),
        };
        foreach (var (prevField, header, firstField) in boundaries)
        {
            var prevIdx = output.IndexOf(prevField, StringComparison.Ordinal);
            var headerIdx = output.IndexOf(header, StringComparison.Ordinal);
            var firstIdx = output.IndexOf(firstField, StringComparison.Ordinal);
            await Assert.That(prevIdx).IsGreaterThanOrEqualTo(0);
            await Assert.That(headerIdx).IsGreaterThan(prevIdx)
                .Because($"'{header}' must render below '{prevField}'");
            await Assert.That(firstIdx).IsGreaterThan(headerIdx)
                .Because($"'{firstField}' must render below '{header}'");
        }
    }

    [Test]
    public async Task EditingBackupTogglesAndSchedule()
    {
        // Happy-path edit of every row in the five-row backup section (37..41).
        var console = NewConsole();
        EditField(console, fieldIndex: 37, "y");                           // Enabled := true
        EditField(console, fieldIndex: 38, "Mon..Fri 03:30");              // Schedule
        EditField(console, fieldIndex: 39, "30");                          // RetainCount
        EditField(console, fieldIndex: 40, "/srv/backups/interfold");     // Directory
        EditField(console, fieldIndex: 41, "y");                           // AutostartServer := true
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Backup.Enabled).IsTrue();
        await Assert.That(config.Backup.Schedule).IsEqualTo("Mon..Fri 03:30");
        await Assert.That(config.Backup.RetainCount).IsEqualTo(30);
        await Assert.That(config.Backup.Directory).IsEqualTo("/srv/backups/interfold");
        await Assert.That(config.Backup.AutostartServer).IsTrue();
    }

    [Test]
    public async Task BackupRetainPromptRejectsOutOfRangeValues()
    {
        // 0 is outside [1..1000]; PromptInt re-prompts and the second answer sticks.
        var console = NewConsole();
        EditField(console, fieldIndex: 39, "0", "7");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Backup.RetainCount).IsEqualTo(7);
    }

    [Test]
    public async Task BackupSectionEmptyDirectoryDefaults()
    {
        // Empty Directory = the "use {outputDir}/backups" sentinel — operators who ignore
        // the section entirely (the common case) still produce a valid config.
        var console = NewConsole();
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Backup.Directory).IsEqualTo(string.Empty);
        await Assert.That(config.Backup.Enabled).IsFalse();
        await Assert.That(config.Backup.AutostartServer).IsFalse();
    }

    [Test]
    public async Task EditingNodeGroupCapturesValue()
    {
        // Happy-path edit; rejection covered by NodeGroupPromptEnforcesChoices below.
        var console = NewConsole();
        EditField(console, fieldIndex: 22, "primary");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Cluster.NodeGroup).IsEqualTo(NodeGroup.Primary);
    }

    [Test]
    public async Task NodeGroupPromptEnforcesChoices()
    {
        // AddChoices re-prompts on non-listed values (same shape as ScyllaKeyspace).
        var console = NewConsole();
        EditField(console, fieldIndex: 22, "guardian", "sidecar");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Cluster.NodeGroup).IsEqualTo(NodeGroup.Sidecar);
    }

    [Test]
    public async Task EditingStorageAndObservabilityCapturesValues()
    {
        // Non-empty round-trip; blank-state pinned by ConfirmingFormImmediatelyUsesDefaults.
        var console = NewConsole();
        EditField(console, fieldIndex: 23, "http://otel-collector:4317");
        EditField(console, fieldIndex: 24, "/var/lib/interfold/avatars");
        EditField(console, fieldIndex: 25, "https://cdn.example.com/avatars/");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Observability.OtlpEndpoint).IsEqualTo("http://otel-collector:4317");
        await Assert.That(config.Storage.AvatarStorageRoot).IsEqualTo("/var/lib/interfold/avatars");
        await Assert.That(config.Storage.AvatarPublicBase).IsEqualTo("https://cdn.example.com/avatars/");
    }

    [Test]
    public async Task EditingTuningIntsCapturesValues()
    {
        // Row 26 uses PromptNullableInt; the other four use PromptInt. All five round-trip.
        var console = NewConsole();
        EditField(console, fieldIndex: 26, "131072");
        EditField(console, fieldIndex: 27, "5");
        EditField(console, fieldIndex: 28, "250");
        EditField(console, fieldIndex: 29, "3000");
        EditField(console, fieldIndex: 30, "16");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Socket.BatchBytesThreshold).IsEqualTo(131072);
        await Assert.That(config.Persistence.DbRetryAttempts).IsEqualTo(5);
        await Assert.That(config.Persistence.DbRetryInitialDelayMs).IsEqualTo(250);
        await Assert.That(config.Persistence.DbRetryMaxDelayMs).IsEqualTo(3000);
        await Assert.That(config.Persistence.HydrationMaxConcurrency).IsEqualTo(16);
    }

    [Test]
    public async Task BatchBytesThresholdAcceptsBlankAsNull()
    {
        // Blank on row 26 must clear to null (PromptNullableInt contract), not fall back to the existing value.
        var console = NewConsole();
        EditField(console, fieldIndex: 26, string.Empty);
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Socket.BatchBytesThreshold).IsNull();
    }

    [Test]
    public async Task DbRetryAttemptsRePromptsOnOutOfRangeInt()
    {
        // 9999 breaches the [1..100] bound on row 27; second answer sticks. Pins that the
        // tuning fields share PromptInt's validator with the port rows.
        var console = NewConsole();
        EditField(console, fieldIndex: 27, "9999", "5");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Persistence.DbRetryAttempts).IsEqualTo(5);
        await Assert.That(console.Output).Contains("must be an integer in");
    }

    [Test]
    public async Task FormShowsEditedValueInMenuRow()
    {
        // Post-edit re-render must echo the freshly-typed value on the row.
        var console = NewConsole();
        EditField(console, fieldIndex: 7, "5005");
        ConfirmForm(console);

        PromptWithoutDetection(console);

        await Assert.That(console.Output).Contains("5005");
    }

    [Test]
    public async Task FormMasksOAuthSecretsInMenuRow()
    {
        // Runs unmasked (maskSecrets:false) to prove the Mask() Show() callback is what
        // hides the value in the row — Spectre's per-field Secret() is covered separately by
        // MaskSecretsHidesOAuthEchoInPromptOutput. The raw secret WILL appear in the
        // TextPrompt echo; the guard is that it never appears NEXT TO the label.
        const string secret = "google-secret-xyz";
        var console = NewConsole();
        EditField(console, fieldIndex: 32, secret);
        ConfirmForm(console);

        PromptWithoutDetection(console);

        var output = console.Output;
        // <set> next to the edited row, <empty> next to the two unedited ones.
        await Assert.That(output).Contains("<set>");
        await Assert.That(output).Contains("<empty>");

        // The actual masking guard.
        var leakedMenuRow = new Regex(@"Google OAuth client secret\s+google-secret-xyz");
        await Assert.That(leakedMenuRow.IsMatch(output)).IsFalse();

        // Sanity: without this, the guard above passes vacuously.
        await Assert.That(output).Contains(secret);
    }

    [Test]
    public async Task EditingUpdateSectionCapturesValues()
    {
        // Happy-path edit of every row in the five-row update section (42..46).
        var console = NewConsole();
        EditField(console, fieldIndex: 42, "y");                                    // Enabled := true
        EditField(console, fieldIndex: 43, "300");                                  // HealthCheckTimeoutSeconds
        EditField(console, fieldIndex: 44, "y");                                    // AutoRestoreOnFailure := true
        EditField(console, fieldIndex: 45, "n");                                    // RecreateOnUpdate := false
        EditField(console, fieldIndex: 46, "interfold-api,octocon-web");            // Services
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Update.Enabled).IsTrue();
        await Assert.That(config.Update.HealthCheckTimeoutSeconds).IsEqualTo(300);
        await Assert.That(config.Update.AutoRestoreOnFailure).IsTrue();
        await Assert.That(config.Update.RecreateOnUpdate).IsFalse();
        await Assert.That(config.Update.Services.Length).IsEqualTo(2);
        await Assert.That(config.Update.Services).Contains("interfold-api");
        await Assert.That(config.Update.Services).Contains("octocon-web");
    }

    [Test]
    public async Task UpdateHealthCheckTimeoutRePromptsOnOutOfRange()
    {
        // 9999 breaches the [1..3600] bound; second answer sticks.
        var console = NewConsole();
        EditField(console, fieldIndex: 43, "9999", "60");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Update.HealthCheckTimeoutSeconds).IsEqualTo(60);
        await Assert.That(console.Output).Contains("must be an integer in");
    }

    [Test]
    public async Task UpdateServicesPromptRejectsUnknownEntry()
    {
        // Unknown entry re-prompts against ValidUpdateServices; second answer sticks.
        var console = NewConsole();
        EditField(console, fieldIndex: 46,
            "msg-db,not-a-real-service",
            "msg-db,scylla");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Update.Services.Length).IsEqualTo(2);
        await Assert.That(config.Update.Services).Contains("msg-db");
        await Assert.That(config.Update.Services).Contains("scylla");
        await Assert.That(console.Output).Contains("not a known compose service");
    }

    [Test]
    public async Task UpdateServicesBlankClearsWhitelist()
    {
        // Blank = "every service" (stored as empty array) — the un-scope path in the UI.
        var console = NewConsole();
        EditField(console, fieldIndex: 46, string.Empty);
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Update.Services.Length).IsEqualTo(0);
    }

    [Test]
    public async Task UpdateSectionEmptyDefaults()
    {
        // Locks the "manual only" property-initialiser defaults so a flipped default is caught.
        var console = NewConsole();
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Update.Enabled).IsFalse();
        await Assert.That(config.Update.HealthCheckTimeoutSeconds).IsEqualTo(180);
        await Assert.That(config.Update.AutoRestoreOnFailure).IsFalse();
        await Assert.That(config.Update.RecreateOnUpdate).IsTrue();
        await Assert.That(config.Update.Services.Length).IsEqualTo(0);
    }

    [Test]
    public async Task MaskSecretsHidesOAuthEchoInPromptOutput()
    {
        // Prod path (maskSecrets:true) → Secret('*') masks the echo. PushTextWithEnter's
        // per-char + Enter sequence matches what ReadKey consumes.
        const string secret = "google-secret-xyz";
        var console = NewConsole();
        EditField(console, fieldIndex: 32, secret);
        ConfirmForm(console);

        var config = PromptWithoutDetection(console, maskSecrets: true);

        // Value still lands on the config; masking is display-only.
        await Assert.That(config.OAuth.GoogleClientSecret).IsEqualTo(secret);
        await Assert.That(console.Output).DoesNotContain(secret);
    }

    /// <summary>
    /// Minimal service-account fixture so the scanner's <c>"type": "service_account"</c>
    /// sniff finds a valid candidate. FirebasePhase-level parsing is validated separately.
    /// </summary>
    private const string FirebaseServiceAccountFixture = /*lang=json,strict*/ """
    {
      "type": "service_account",
      "project_id": "octocon-test",
      "private_key_id": "abc",
      "private_key": "-----BEGIN PRIVATE KEY-----\nfake\n-----END PRIVATE KEY-----\n",
      "client_email": "sa@octocon.iam.gserviceaccount.com",
      "client_id": "1",
      "auth_uri": "https://accounts.google.com/o/oauth2/auth",
      "token_uri": "https://oauth2.googleapis.com/token"
    }
    """;

    private static string NewFirebaseWizardTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "firebase-wizard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Test]
    public async Task FirebaseAutoDetectBranchPopulatesAllFourPathsFromFolder()
    {
        // End-to-end drive of the scanner → wizard → config wiring on the auto-detect branch.
        var folder = NewFirebaseWizardTempDir();
        var android = Path.Combine(folder, "google-services.json");
        var ios = Path.Combine(folder, "GoogleService-Info.plist");
        var web = Path.Combine(folder, "firebase-web-config.json");
        var sa = Path.Combine(folder, "octocon-firebase-adminsdk-abc.json");
        File.WriteAllText(android, "{}");
        File.WriteAllText(ios, "<plist/>");
        File.WriteAllText(web, "{}");
        File.WriteAllText(sa, FirebaseServiceAccountFixture);

        var console = NewConsole();
        Navigate(console, downArrows: 47);
        // Auto-detect is the first choice — Enter without a DownArrow.
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter(folder);
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Firebase.AndroidConfigPath).IsEqualTo(Path.GetFullPath(android));
        await Assert.That(config.Firebase.IosConfigPath).IsEqualTo(Path.GetFullPath(ios));
        await Assert.That(config.Firebase.WebConfigPath).IsEqualTo(Path.GetFullPath(web));
        await Assert.That(config.Firebase.ServiceAccountPath).IsEqualTo(Path.GetFullPath(sa));
    }

    [Test]
    public async Task FirebasePerFileBranchWritesFourExplicitPaths()
    {
        // Per-file branch: four sequential TextPrompts, each guarded by File.Exists.
        var folder = NewFirebaseWizardTempDir();
        var android = Path.Combine(folder, "google-services.json");
        var ios = Path.Combine(folder, "GoogleService-Info.plist");
        var web = Path.Combine(folder, "firebase-web-config.json");
        var sa = Path.Combine(folder, "service-account.json");
        File.WriteAllText(android, "{}");
        File.WriteAllText(ios, "<plist/>");
        File.WriteAllText(web, "{}");
        File.WriteAllText(sa, FirebaseServiceAccountFixture);

        var console = NewConsole();
        Navigate(console, downArrows: 47);
        // Per-file is the 2nd choice — one DownArrow before Enter.
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter(android);
        console.Input.PushTextWithEnter(ios);
        console.Input.PushTextWithEnter(web);
        console.Input.PushTextWithEnter(sa);
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Firebase.AndroidConfigPath).IsEqualTo(android);
        await Assert.That(config.Firebase.IosConfigPath).IsEqualTo(ios);
        await Assert.That(config.Firebase.WebConfigPath).IsEqualTo(web);
        await Assert.That(config.Firebase.ServiceAccountPath).IsEqualTo(sa);
    }

    [Test]
    public async Task FirebaseCancelBranchLeavesSectionAtDefaults()
    {
        // Cancel is the 4th choice — three DownArrows. Every *Path must stay empty.
        var console = NewConsole();
        Navigate(console, downArrows: 47);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Firebase.AndroidConfigPath).IsEmpty();
        await Assert.That(config.Firebase.IosConfigPath).IsEmpty();
        await Assert.That(config.Firebase.WebConfigPath).IsEmpty();
        await Assert.That(config.Firebase.ServiceAccountPath).IsEmpty();
    }
}
