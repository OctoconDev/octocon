using System.Net;
using System.Text.RegularExpressions;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using Spectre.Console;
using Spectre.Console.Testing;
using TUnit.Core;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Drives <see cref="ConfigPhase.PromptForConfig(Spectre.Console.IAnsiConsole, bool)"/> with an
/// in-memory <see cref="TestConsole"/> so the Spectre.Console-driven navigable form is exercised
/// without standing up a real TTY.
///
/// Interaction model after the form-only redesign:
/// <list type="bullet">
///   <item>The form is a single <c>SelectionPrompt&lt;int&gt;</c> with 48 field rows grouped
///         under 11 section headers + a trailing <c>Confirm and save</c> entry. Section headers
///         are inert (not selectable); arrow-key navigation skips them.</item>
///   <item>Cursor starts at field index 0 (<c>Output directory</c>) — <see cref="SelectionPrompt{T}.DefaultValue"/>
///         defaults to <c>default(int)</c>.</item>
///   <item>After every edit the form re-renders with the cursor back at field 0; tests can
///         therefore use absolute navigation distances on every iteration.</item>
/// </list>
///
/// Field row order (0-based, used as the <see cref="Navigate"/> distance from the cursor's
/// starting position):
/// <list type="number">
///   <item>0  Output directory                       (Deployment)</item>
///   <item>1  Public domain(s)                       (Deployment)</item>
///   <item>2  Root CA subject                        (Deployment)</item>
///   <item>3  Leaf cert validity (years)             (Deployment)</item>
///   <item>4  Install root CA into trust store       (Deployment)</item>
///   <item>5  Include octocon-web container          (Deployment)</item>
///   <item>6  Terminate HTTPS at octocon-web         (Deployment)</item>
///   <item>7  API HTTP port                          (Ports)</item>
///   <item>8  API HTTPS port                         (Ports)</item>
///   <item>9  Web HTTP port                          (Ports)</item>
///   <item>10 Web HTTPS port                         (Ports)</item>
///   <item>11 Postgres host port                     (Ports)</item>
///   <item>12 Scylla/Cassandra host port             (Ports)</item>
///   <item>13 Database mode                          (Database)</item>
///   <item>14 Postgres application DB name           (Database)</item>
///   <item>15 Cluster name                           (Database)</item>
///   <item>16 Scylla keyspace (region)               (Database)</item>
///   <item>17 OAuth callback base URL                (API)</item>
///   <item>18 JWT authority (iss claim)              (API)</item>
///   <item>19 JWT audience (aud claim)               (API)</item>
///   <item>20 CORS allowed origins                   (API)</item>
///   <item>21 Pre-built Interfold API image          (API)</item>
///   <item>22 Cluster node group                     (Cluster &amp; telemetry)</item>
///   <item>23 OTLP endpoint                          (Cluster &amp; telemetry)</item>
///   <item>24 Avatar storage root (container path)   (Storage)</item>
///   <item>25 Avatar public base URL                 (Storage)</item>
///   <item>26 Socket batch flush threshold (bytes)   (Performance tuning)</item>
///   <item>27 DB retry attempts                      (Performance tuning)</item>
///   <item>28 DB retry initial delay (ms)            (Performance tuning)</item>
///   <item>29 DB retry max delay (ms)                (Performance tuning)</item>
///   <item>30 Hydration max concurrency              (Performance tuning)</item>
///   <item>31 Google OAuth client ID                 (OAuth credentials)</item>
///   <item>32 Google OAuth client secret             (OAuth credentials)</item>
///   <item>33 Discord OAuth client ID                (OAuth credentials)</item>
///   <item>34 Discord OAuth client secret            (OAuth credentials)</item>
///   <item>35 Apple OAuth client ID                  (OAuth credentials)</item>
///   <item>36 Apple OAuth client secret              (OAuth credentials)</item>
///   <item>37 Scheduled backups enabled              (Backup &amp; autostart)</item>
///   <item>38 Backup schedule (OnCalendar)           (Backup &amp; autostart)</item>
///   <item>39 Backup retention (count per component) (Backup &amp; autostart)</item>
///   <item>40 Backup directory (absolute, blank=default) (Backup &amp; autostart)</item>
///   <item>41 Autostart server on boot              (Backup &amp; autostart)</item>
///   <item>42 Chain updates after backup             (Updates)</item>
///   <item>43 Health-check timeout (seconds)         (Updates)</item>
///   <item>44 Auto-restore on failure                (Updates)</item>
///   <item>45 Recreate containers on update          (Updates)</item>
///   <item>46 Update service whitelist (blank=all)   (Updates)</item>
///   <item>47 Firebase push notifications            (Firebase)</item>
/// </list>
/// Navigate(48) lands on the trailing <c>Confirm and save</c> entry (one DownArrow per
/// selectable row past field 0). Helpers <see cref="Navigate"/> / <see cref="ConfirmForm"/> /
/// <see cref="EditField"/> below wrap the key sequences so the test bodies stay focused on
/// "what is being edited", not "how many DownArrows that takes".
/// </summary>
public sealed class ConfigInteractivePromptTests
{
    /// <summary>Number of selectable field rows in the navigable form.</summary>
    private const int FieldCount = 48;

    /// <summary>
    /// Builds a fresh interactive <see cref="TestConsole"/> tall enough to render the entire
    /// 60-row navigable form (48 fields + 11 section headers + Confirm entry) without Spectre
    /// paginating it. Default <see cref="TestConsole"/> height is 24 lines, which would clamp
    /// the menu to a single page and hide the top section header by the time the cursor
    /// reaches Confirm — breaking the "every label is in the captured output" assertions.
    /// Each test seeds the input queue after construction via the navigation helpers below.
    /// </summary>
    private static TestConsole NewConsole()
    {
        var c = new TestConsole();
        c.Interactive();
        // Bump well above the form's 60-row footprint so the form never paginates in
        // tests (pagination clips top rows out of the captured output and breaks the
        // "every label is in the output" assertions).
        c.Profile.Height = 120;
        c.Profile.Width = 130;
        return c;
    }

    /// <summary>
    /// Pushes <paramref name="downArrows"/> down-arrow key events followed by Enter, simulating
    /// the operator navigating from the cursor's current position to a row N steps below and
    /// activating it. Field index <c>N</c> sits exactly <c>N</c> DownArrows below the cursor's
    /// initial position (field 0); the trailing <c>Confirm and save</c> entry sits at
    /// <see cref="FieldCount"/> DownArrows.
    /// </summary>
    private static void Navigate(TestConsole c, int downArrows)
    {
        for (var i = 0; i < downArrows; i++)
            c.Input.PushKey(ConsoleKey.DownArrow);
        c.Input.PushKey(ConsoleKey.Enter);
    }

    /// <summary>
    /// Selects the trailing <c>Confirm and save</c> entry from the form's default starting
    /// position (field 0). Tests call this exactly once per form invocation to terminate the
    /// navigable loop.
    /// </summary>
    private static void ConfirmForm(TestConsole c) => Navigate(c, FieldCount);

    /// <summary>
    /// Opens the per-field editor for <paramref name="fieldIndex"/> and pushes
    /// <paramref name="answers"/> as the answer line(s) the field's prompt will consume. The
    /// field's editor reads via <c>ReadLine</c> (or <c>ReadKey</c> for the masked OAuth path),
    /// which both consume from the same input queue <see cref="TestConsoleInput.PushTextWithEnter"/>
    /// populates. Use multiple <paramref name="answers"/> when the field re-prompts on invalid
    /// input — e.g. <c>EditField(c, 6, "bad", "5005")</c> for an int re-prompt.
    /// </summary>
    private static void EditField(TestConsole c, int fieldIndex, params string[] answers)
    {
        Navigate(c, fieldIndex);
        foreach (var a in answers)
            c.Input.PushTextWithEnter(a);
    }

    /// <summary>
    /// Calls <see cref="ConfigPhase.PromptForConfig(IAnsiConsole, bool, Func{IPAddress?}?, Func{string?}?)"/>
    /// with null-returning <c>localAddressProbe</c> AND <c>hostnameProbe</c> so neither the
    /// device-IP nor the mDNS-hostname auto-default fires during unit tests. Every test below
    /// that doesn't specifically exercise the auto-default goes through this wrapper — without
    /// it, a CI runner with a routable NIC would silently pre-populate <c>Deployment.Hosts</c>
    /// with the runner's primary IP and break the existing "fresh config = empty Hosts"
    /// assertion in <see cref="ConfirmingFormImmediatelyUsesDefaults"/> (and incidentally
    /// pollute the derived-URL menu rows in tests that don't edit hosts). The
    /// <c>hostnameProbe</c> default here is redundant (the prompt's own default is
    /// <c>() =&gt; null</c>) but stated explicitly so a future refactor that flips
    /// PromptForConfig's default to a live detector doesn't silently regress these tests.
    /// The tests that DO want a detected IP call
    /// <see cref="ConfigPhase.PromptForConfig(IAnsiConsole, bool, Func{IPAddress?}?, Func{string?}?)"/>
    /// directly with an explicit fake.
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
        // Hosts has no shipped default placeholder — confirming the form without editing the
        // Hosts row leaves Hosts as []. Validate fails fast on this state downstream, which is
        // the explicit fail-fast contract documented in DeploymentSection.Hosts. Note: this
        // test routes through PromptWithoutDetection which suppresses the runtime device-IP
        // pre-fill; in production an interactive bootstrap on a box with a routable NIC sees
        // the detected IP as the row's default (see DetectedDeviceIpPreFillsHostsOnFreshBootstrap).
        await Assert.That(config.Deployment.Hosts.Count).IsEqualTo(0);

        // Ports defaults
        await Assert.That(config.Ports.ApiHttp).IsEqualTo(5000);
        await Assert.That(config.Ports.ApiHttps).IsEqualTo(5001);
        await Assert.That(config.Ports.WebHttp).IsEqualTo(8080);
        await Assert.That(config.Ports.WebHttps).IsEqualTo(8081);
        await Assert.That(config.Ports.Postgres).IsEqualTo(4200);
        await Assert.That(config.Ports.Scylla).IsEqualTo(9042);

        // Database / API defaults
        await Assert.That(config.DatabaseMode).IsEqualTo("single");
        await Assert.That(config.PostgresDatabase).IsEqualTo("interfold");
        await Assert.That(config.ClusterName).IsEqualTo("InterfoldCluster");
        await Assert.That(config.ScyllaKeyspace).IsEqualTo("nam");
        await Assert.That(config.ApiImage).IsEqualTo("ghcr.io/azyyyyyy/interfold-api:latest");

        // ApiRuntime defaults are NOT derived inside PromptForConfig itself — the menu's
        // Show callbacks derive them on every redraw, but the stored field is whatever the
        // operator last typed (or empty if they accepted the prompt's derived default).
        // RunAsync / Validate is where the stored field gets the derived value materialised.
        await Assert.That(config.ApiRuntime.CallbackBaseUrl).IsEqualTo(string.Empty);
        await Assert.That(config.ApiRuntime.JwtAuthority).IsEqualTo(string.Empty);
        // JwtAudience has a hardcoded property-initialiser default; the form just echoes it.
        await Assert.That(config.ApiRuntime.JwtAudience).IsEqualTo("octocon");
        await Assert.That(config.ApiRuntime.CorsAllowedOrigins.Count).IsEqualTo(0);

        // Cluster & telemetry / Storage / Performance tuning defaults. NodeGroup +
        // four DB-retry/hydration tuning ints have non-null property-initialiser
        // defaults that match the API's compile-time fallbacks; the three "disabled
        // when blank" string fields (Avatar* + OtlpEndpoint) and the nullable
        // BatchBytesThreshold default to empty/null so leaving them untouched
        // reproduces the pre-bootstrapper "env var unset" behaviour 1:1.
        await Assert.That(config.Cluster.NodeGroup).IsEqualTo("auxiliary");
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

        // Update-section defaults — every field defaults to a "manual only" stance so a
        // stock bootstrap behaves identically to pre-feature deployments.
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
        // Mixed input: IPv4 literal + IPv6 CIDR. Both must survive the validator and reach the
        // stored list verbatim — the parser pins their kinds (Ipv4 / Ipv6Cidr) at config-load
        // time, and the prompt's responsibility ends at preserving the operator's raw text.
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
        // The interactive prompt pre-fills "Public host(s)" with the device's primary IP so the
        // operator's first-run experience is a single Enter to accept the detected LAN address.
        // We inject a deterministic probe so the test doesn't depend on the runner's NICs.
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
        // The stored Hosts entry must be the bare literal — HostParser accepts either form for
        // IPv6 (bracketed or bare), but downstream consumers expect the bare form: the SAN
        // encoder uses entry.Ip directly, and URL derivation in ResolveDerivedDefaults handles
        // its own bracket-wrapping via HostParser.ToUrlHost. Storing "[::1]" would round-trip
        // into the JSON file with the brackets and silently surprise anyone hand-editing it.
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
        // Pre-fill is just a default — the operator can still edit the row to whatever they
        // want, and the typed value MUST win. Sanity-check that the auto-default doesn't bake
        // itself into the stored config when the operator explicitly types a replacement.
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
        // The detector can legitimately return null (loopback-only test runner, air-gapped box,
        // every NIC filtered as virtual). In that case the prompt must NOT invent a fallback —
        // Hosts stays [] and Validate fails fast downstream, which is the documented contract
        // on DeploymentSection.Hosts. This is the same shape PromptWithoutDetection exercises,
        // but pinned as an explicit test so a future "default to 127.0.0.1 when detection fails"
        // refactor gets caught.
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
        // The menu rendering is where the operator sees the auto-default before deciding to
        // accept or override. Pin that the detected IP appears in the captured output next to
        // the "Public host(s)" label so a future refactor that bypasses the Hosts pre-fill (or
        // accidentally puts the detected value in the wrong row) is caught visually too.
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
        // Two-probe pre-fill: when the pre-prompt banner reports mDNS is working AND the
        // device has a routable NIC, the hosts seed contains BOTH the {hostname}.local name
        // (first, so HostParser.PickPrimary latches it as the primary host for leaf-cert /
        // derived-URL purposes) AND the primary IP (second, for LAN-address-based reachability).
        // Order matters — a regression that swaps them would silently change every derived
        // URL to point at the IP instead of the hostname.
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
        // The menu row shows both values comma-joined (Show callback = string.Join(",", Hosts)).
        // Pin the exact display so a future refactor that (say) prints only Hosts[0] gets caught.
        await Assert.That(Regex.IsMatch(console.Output, @"Public host\(s\)\s+workstation\.local,192\.168\.1\.42")).IsTrue();
    }

    [Test]
    public async Task HostsPrefillOmitsHostnameWhenProbeReturnsNull()
    {
        // The pre-prompt banner returns null when mDNS is unavailable (or the operator declined
        // the install offer). In that case only the detected IP lands in the pre-fill — the
        // hostname is deliberately omitted rather than pre-filled with an unresolvable name.
        // Documents the "banner said mDNS is unavailable" flow and pins that a null probe
        // never becomes an accidental empty-string / null entry in the Hosts list.
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
        // OAuth rows are paired per provider (ID then secret) - the secret rows therefore sit at
        // 32 / 34 / 36 (immediately after each provider's matching ID row at 31 / 33 / 35).
        var console = NewConsole();
        EditField(console, fieldIndex: 32, "google-secret-xyz");
        EditField(console, fieldIndex: 34, "discord-secret-abc");
        EditField(console, fieldIndex: 36, "apple-secret-jwt");
        ConfirmForm(console);

        // maskSecrets:false (the default) keeps the prompt off the ReadKey path so PushTextWithEnter
        // suffices — matches the in-test behaviour we want.
        var config = PromptWithoutDetection(console);

        await Assert.That(config.OAuth.GoogleClientSecret).IsEqualTo("google-secret-xyz");
        await Assert.That(config.OAuth.DiscordClientSecret).IsEqualTo("discord-secret-abc");
        await Assert.That(config.OAuth.AppleClientSecret).IsEqualTo("apple-secret-jwt");
    }

    [Test]
    public async Task EditingOAuthClientIdsCapturesValues()
    {
        // Client IDs are public per-provider identifiers - paired with the matching secrets but
        // sourced via the plain PromptStr path (no masking, no <set>/<empty> indirection on the
        // menu row). The three ID rows sit at the start of each provider's pair: 31 / 33 / 35.
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
        // Pins the parity with the secret rows: an OAuth client ID row that was never set must
        // render the <empty> marker (same as the matching secret), so the menu makes the
        // unset-vs-set distinction visible at a glance. Without this, an empty ID just paints
        // a blank cell next to the label, which reads as "no row" rather than "row exists but
        // is unset" — and operators who tab through the form can miss that they need to fill
        // both halves of a provider's credentials.
        var console = NewConsole();
        ConfirmForm(console);

        PromptWithoutDetection(console);

        // The menu row format is "<padded label> <value>" after markup stripping, so a guard
        // regex anchored on each ID's label catches a regression that would leave the value
        // column blank or non-<empty>.
        var output = console.Output;
        await Assert.That(Regex.IsMatch(output, @"Google OAuth client ID\s+<empty>")).IsTrue();
        await Assert.That(Regex.IsMatch(output, @"Discord OAuth client ID\s+<empty>")).IsTrue();
        await Assert.That(Regex.IsMatch(output, @"Apple OAuth client ID\s+<empty>")).IsTrue();
    }

    [Test]
    public async Task FormShowsOAuthClientIdsVerbatimInMenuRow()
    {
        // OAuth client IDs are NOT secrets - they end up in the OAuth redirect URL and are
        // publicly visible at runtime, so the menu row should echo them as-is (unlike the secret
        // rows, which collapse to <set>/<empty>). The post-edit form re-render must contain the
        // literal Google client ID we typed, NOT a "<set>" marker.
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
        // IncludeWeb (row 5) is the HTTP-only opt-in for the octocon-web container. Independent
        // from WebHttps (row 6) — flipping IncludeWeb=true while WebHttps stays false ships the
        // wasm container in HTTP-only mode (the debugging / external-TLS-proxy variant). This
        // test pins the prompt round-trips a typed "y" through the form onto the config without
        // also promoting WebHttps.
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
        // EditField pushes both answers into the same field's editor; the inline ValidationErrorMessage
        // re-prompts after "bad" and the second answer (5005) is what should stick.
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

        await Assert.That(config.DatabaseMode).IsEqualTo("multi");
    }

    [Test]
    public async Task EditingScyllaKeyspaceCapturesValue()
    {
        // Scylla keyspace (row 16) is the per-instance region identity — operator picks one of
        // the seven valid regional values, defaulted to "nam". The AddChoices restriction is
        // covered by the rejection test below; this one just proves the happy-path edit.
        var console = NewConsole();
        EditField(console, fieldIndex: 16, "eur");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.ScyllaKeyspace).IsEqualTo("eur");
    }

    [Test]
    public async Task ScyllaKeyspacePromptEnforcesChoices()
    {
        // AddChoices on the underlying TextPrompt enforces the seven-value allow-list. Typing
        // anything else re-prompts; the eventually-accepted value is what sticks.
        var console = NewConsole();
        EditField(console, fieldIndex: 16, "ant", "gdpr");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.ScyllaKeyspace).IsEqualTo("gdpr");
    }

    [Test]
    public async Task EditingApiRuntimeCapturesValues()
    {
        // The four ApiRuntime rows: callback URL (17), JWT authority (18), JWT audience (19),
        // CORS allowed origins (20). Each goes through its dedicated prompt (PromptStr for
        // the three single-value fields, PromptCorsAllowedOrigins for the comma-separated
        // list). All four must round-trip through the form and land on the config verbatim.
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
        // The four ApiRuntime show callbacks call ResolveDerivedDefaults on a clone so the
        // menu row paints the derived default next to its label even when the stored field
        // is still empty. Hosts has no shipped default, so the test first populates it via
        // the Public host(s) row (field index 1); after that, with WebHttps=false and the
        // shipped port defaults (Ports.ApiHttps=5001 / Ports.WebHttp=8080) the derived
        // callback URL is "https://api.example.com:5001" (always https for the API in
        // self-host) and the CORS origin is "http://api.example.com:8080" (web tier).
        var console = NewConsole();
        EditField(console, fieldIndex: 1, "api.example.com");
        ConfirmForm(console);

        PromptWithoutDetection(console);

        var output = console.Output;
        // Same row-format guard as the OAuth-ID <empty> assertions: anchor on the label so
        // a regression that paints a blank value cell or the wrong derived string fails loudly.
        await Assert.That(Regex.IsMatch(output, @"OAuth callback base URL\s+https://api\.example\.com:5001")).IsTrue();
        await Assert.That(Regex.IsMatch(output, @"JWT authority \(iss claim\)\s+https://api\.example\.com:5001")).IsTrue();
        // CORS row derives one entry per non-CIDR host, joined with ',' for the menu display.
        // The single-host input produces one entry on the web scheme + port.
        await Assert.That(Regex.IsMatch(output, @"CORS allowed origins\s+http://api\.example\.com:8080")).IsTrue();
    }

    [Test]
    public async Task CorsAllowedOriginsRePromptsOnInvalidUri()
    {
        // The inline validator in PromptCorsAllowedOrigins rejects any entry that doesn't
        // parse as an absolute http(s) URI. Typing a bare hostname re-prompts; the second
        // (valid) answer is what sticks.
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
        // The menu rendering is the only chance to catch a dropped field. Confirming the form
        // immediately is enough — the menu is drawn before the operator gets a chance to act.
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
        // OAuth credentials — every provider has BOTH an ID and a secret row, and both
        // must be reachable from the menu. Asserting the suffix "client ID" / "client secret"
        // separately catches a future refactor that drops a label by accident.
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
        // Section headers are rendered by AddChoiceGroup as inert rows above each group. All
        // ten headers (Deployment / Ports / Database / API / Cluster & telemetry / Storage /
        // Performance tuning / OAuth credentials / Backup & autostart / Updates) must appear
        // in the captured output. "API" subsumes the old single-row "API image" section now
        // that the four ApiRuntime fields share the section.
        var console = NewConsole();
        ConfirmForm(console);

        PromptWithoutDetection(console);

        var output = console.Output;
        await Assert.That(output).Contains("Deployment");
        await Assert.That(output).Contains("Ports");
        await Assert.That(output).Contains("Database");
        // Use regex with the "---" framing so this doesn't match the literal token inside the
        // "OAuth callback base URL" label / the "API HTTP port" label / etc.
        await Assert.That(Regex.IsMatch(output, @"---\s+API\s+---")).IsTrue();
        await Assert.That(output).Contains("Cluster & telemetry");
        // Storage as a header must be distinguishable from the "Avatar storage root" label —
        // the "---" framing pins the header context.
        await Assert.That(Regex.IsMatch(output, @"---\s+Storage\s+---")).IsTrue();
        await Assert.That(output).Contains("Performance tuning");
        await Assert.That(output).Contains("OAuth credentials");
        // New Backup & autostart section sits after the credentials group; the framing is
        // the same as Storage.
        await Assert.That(output).Contains("Backup & autostart");
        // Updates section sits at the tail; framed same as the others so this line matches
        // the header, not e.g. the "Auto-restore" label in a row value.
        await Assert.That(Regex.IsMatch(output, @"---\s+Updates\s+---")).IsTrue();
        // The title is part of every form render — assert it too so a future refactor that
        // accidentally drops the Title() call gets flagged immediately.
        await Assert.That(output).Contains("Configure interfold.bootstrap.json");
        // And the Confirm entry.
        await Assert.That(output).Contains("Confirm and save");
    }

    [Test]
    public async Task EditingBackupTogglesAndSchedule()
    {
        // Five-row backup section (rows 37..41). Exercises a happy-path edit of every row so
        // the round-trip through PromptForConfig captures the operator input verbatim.
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
        // PromptInt re-prompts on values outside the [1..1000] band. The first input is bad
        // (the validator's lower bound is 1) and the operator types a valid value second.
        var console = NewConsole();
        EditField(console, fieldIndex: 39, "0", "7");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Backup.RetainCount).IsEqualTo(7);
    }

    [Test]
    public async Task BackupSectionEmptyDirectoryDefaults()
    {
        // Confirming the form without editing the directory row leaves it at the empty-string
        // default — the "use {outputDir}/backups" sentinel. Important because operators who
        // ignore the section entirely (the most common case) still produce a valid config.
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
        // NodeGroup (row 22) is the cluster-role identity. AddChoices on the underlying
        // TextPrompt enforces the three-value allow-list (primary / auxiliary / sidecar);
        // this test exercises the happy path. The rejection / re-prompt behaviour is
        // covered by NodeGroupPromptEnforcesChoices below.
        var console = NewConsole();
        EditField(console, fieldIndex: 22, "primary");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Cluster.NodeGroup).IsEqualTo("primary");
    }

    [Test]
    public async Task NodeGroupPromptEnforcesChoices()
    {
        // AddChoices restricts the prompt to {primary, auxiliary, sidecar}; an unknown
        // value re-prompts and the eventually-accepted value sticks. Matches the
        // ScyllaKeyspace AddChoices behaviour pattern.
        var console = NewConsole();
        EditField(console, fieldIndex: 22, "guardian", "sidecar");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Cluster.NodeGroup).IsEqualTo("sidecar");
    }

    [Test]
    public async Task EditingStorageAndObservabilityCapturesValues()
    {
        // Storage (rows 24 / 25) and Observability (row 23) are plain PromptStr rows — empty
        // input is allowed (signals "feature disabled" on the API side). This test pins that
        // a non-empty value round-trips through the form verbatim; the empty-state behaviour
        // is locked down by ConfirmingFormImmediatelyUsesDefaults above.
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
        // The four DB-retry / hydration tuning rows are plain PromptInt rows; the
        // BatchBytesThreshold row (26) is the nullable PromptNullableInt variant. All five
        // round-trip the typed value through the form.
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
        // PromptNullableInt treats blank input as null (the "use the API's compile-time
        // default" signal). This test pins that contract — typing nothing on row 26 leaves
        // BatchBytesThreshold at null even after an explicit edit invocation. The PromptStr
        // helper used by the other tuning rows would default to the existing value on blank
        // input; the nullable variant explicitly clears.
        var console = NewConsole();
        EditField(console, fieldIndex: 26, string.Empty);
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Socket.BatchBytesThreshold).IsNull();
    }

    [Test]
    public async Task DbRetryAttemptsRePromptsOnOutOfRangeInt()
    {
        // PromptInt's inline validator enforces the 1..100 bound on row 27 — typing 9999
        // re-prompts and the second (valid) answer sticks. Same pattern as the port re-prompt
        // test above; this one pins the new tuning fields use the same validator path.
        var console = NewConsole();
        EditField(console, fieldIndex: 27, "9999", "5");
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Persistence.DbRetryAttempts).IsEqualTo(5);
        // PromptInt's ValidationErrorMessage uses "must be an integer in [N..M]" — pin the
        // shared prefix so a future error-text refactor flags both this test and the
        // matching PortPromptRePromptsOnInvalidInt test.
        await Assert.That(console.Output).Contains("must be an integer in");
    }

    [Test]
    public async Task FormShowsEditedValueInMenuRow()
    {
        // After an edit, the form re-renders with the new value displayed next to the field's
        // label. Editing the API HTTP port to 5005 and then confirming gives the form a chance
        // to render twice — the second render must contain the new value.
        var console = NewConsole();
        EditField(console, fieldIndex: 7, "5005");
        ConfirmForm(console);

        PromptWithoutDetection(console);

        // The literal "5005" appears at least once in the captured output — the form's
        // post-edit re-render echoes the freshly-typed value back into the menu row. (It also
        // appears in the TextPrompt echo, but either source is sufficient evidence of the
        // round-trip working.)
        await Assert.That(console.Output).Contains("5005");
    }

    [Test]
    public async Task FormMasksOAuthSecretsInMenuRow()
    {
        // The menu's row format is "<padded-label> <value>" after markup stripping — so if any
        // form render ever leaked the raw secret into the Google OAuth row's value column, the
        // captured output would match `Google OAuth client secret<whitespace><secret>`. The
        // pattern below is the direct guard against that regression.
        //
        // We use the unmasked test path (maskSecrets:false) here on purpose: it proves the row
        // is masked by the Mask() Show() callback (not by Spectre's per-field Secret() mode).
        // The raw secret WILL appear elsewhere in the output (in the TextPrompt echo for the
        // editor itself, which is why we have a separate MaskSecretsHidesOAuthEchoInPromptOutput
        // test pinning the prod-path Secret() behaviour).
        const string secret = "google-secret-xyz";
        var console = NewConsole();
        // Google OAuth client secret sits at index 32 (immediately after the matching ID at 31).
        EditField(console, fieldIndex: 32, secret);
        ConfirmForm(console);

        PromptWithoutDetection(console);

        var output = console.Output;
        // The post-edit form renders show <set> next to Google OAuth client secret (and <empty>
        // next to the two unedited OAuth secret rows). Both markers must be present somewhere
        // in the output — proves the Mask() Show() callback fired through the converter.
        await Assert.That(output).Contains("<set>");
        await Assert.That(output).Contains("<empty>");

        // The actual masking guard: the menu's "Google OAuth client secret" label is never
        // followed (within the same logical menu row) by the raw secret literal.
        var leakedMenuRow = new Regex(@"Google OAuth client secret\s+google-secret-xyz");
        await Assert.That(leakedMenuRow.IsMatch(output)).IsFalse();

        // Sanity: the raw secret IS present in the unmasked-path output (in the TextPrompt
        // echo) — proving the test actually exercised the editor path it claims to. If this
        // assertion ever flipped, the test would be vacuously passing the masking guard above.
        await Assert.That(output).Contains(secret);
    }

    [Test]
    public async Task EditingUpdateSectionCapturesValues()
    {
        // Five-row update section (rows 42..46). Exercises a happy-path edit of every row
        // so the round-trip through PromptForConfig captures the operator input verbatim.
        // Services row: comma-separated list validated against ValidUpdateServices.
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
        // PromptInt bounded to [1..3600]. First input is 9999 (out of range); the second
        // (valid) answer is what sticks. Same pattern as PortPromptRePromptsOnInvalidInt.
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
        // PromptUpdateServices validates every comma-separated entry against
        // ConfigPhase.ValidUpdateServices. Typing an unknown name re-prompts; the
        // second (valid) answer sticks.
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
        // Blank input signals "every service" — the whitelist is stored as an empty array.
        // Important because operators who set a whitelist once and later want to un-scope
        // an update need a clean way to do it in the interactive form.
        var console = NewConsole();
        EditField(console, fieldIndex: 46, string.Empty);
        ConfirmForm(console);

        var config = PromptWithoutDetection(console);

        await Assert.That(config.Update.Services.Length).IsEqualTo(0);
    }

    [Test]
    public async Task UpdateSectionEmptyDefaults()
    {
        // Confirming the form without touching the update section leaves every field at
        // its property-initialiser default — the "manual only" stance. Locked down here
        // so a future refactor that flips a default gets caught immediately.
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
        // Production passes maskSecrets:true so the per-field prompt itself masks each typed
        // character with `*` instead of echoing it. Spectre.Secret() reads characters via
        // ReadKey; PushTextWithEnter populates the TestConsole input queue with the same
        // per-character + Enter sequence that ReadKey consumes, so the masked path works
        // end-to-end against the in-memory console.
        const string secret = "google-secret-xyz";
        var console = NewConsole();
        // Google OAuth client secret sits at index 32 (immediately after the matching ID at 31).
        EditField(console, fieldIndex: 32, secret);
        ConfirmForm(console);

        var config = PromptWithoutDetection(console, maskSecrets: true);

        // The actual value still lands on the config — masking is purely a display concern.
        await Assert.That(config.OAuth.GoogleClientSecret).IsEqualTo(secret);
        // …but the captured console output never contains the raw secret literal: the prompt
        // echoed `*` chars and the menu row rendered `<set>`.
        await Assert.That(console.Output).DoesNotContain(secret);
    }

    /// <summary>
    /// Minimal-shaped service-account fixture the Firebase wizard tests write out so the
    /// scanner's per-file "type": "service_account" sniff finds a valid candidate. The
    /// FirebasePhase-level parsing isn't exercised here — the wizard's job is purely to
    /// point at files; validation is FirebasePhase's problem downstream.
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
        // End-to-end proof that the Firebase row (index 47) dispatches into the auto-detect
        // branch and writes the scanner's output onto FirebaseSection. Uses a real temp folder
        // with the four canonical filenames so we cover the scanner → wizard → config
        // wiring in one drive.
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
        // Open the Firebase field. Navigate handles the DownArrows + Enter that select the row
        // and open its editor (the SelectionPrompt<string> the wizard runs).
        Navigate(console, downArrows: 47);
        // Wizard's SelectionPrompt starts on "Auto-detect from folder" (first choice) — Enter
        // selects it without any DownArrow.
        console.Input.PushKey(ConsoleKey.Enter);
        // Folder TextPrompt reads a full line.
        console.Input.PushTextWithEnter(folder);
        // Back on the outer form → Confirm and save.
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
        // Per-file branch: four sequential TextPrompts, each validated against
        // File.Exists. Writing real fixture files means the prompts accept the inputs without
        // triggering the validator's re-prompt loop.
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
        // Second choice in the SelectionPrompt — one DownArrow before Enter.
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
        // Cancel branch is fourth in the SelectionPrompt — three DownArrows before Enter. Must
        // leave every FirebaseSection.*Path at its shipped default (empty string).
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
