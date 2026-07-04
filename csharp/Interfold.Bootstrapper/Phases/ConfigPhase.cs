using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;
using Spectre.Console;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// Phase 2 — loads <c>interfold.bootstrap.json</c> from disk or builds it via an interactive
/// Spectre.Console walkthrough (sectioned prompts → review-before-commit table → edit loop),
/// then validates and persists the resulting <see cref="BootstrapConfig"/>.
/// </summary>
internal static class ConfigPhase
{
    public static async Task<BootstrapConfig> RunAsync(BootstrapOptions options, PhaseLogger logger, CancellationToken ct)
    {
        const string Phase = "config";
        logger.PhaseStart(Phase);

        var configPath = options.ConfigPath ?? Path.Combine(options.OutputDir, "interfold.bootstrap.json");
        BootstrapConfig config;
        if (File.Exists(configPath))
        {
            logger.Info($"    loading config from {configPath}");
            var json = await File.ReadAllTextAsync(configPath, ct).ConfigureAwait(false);
            config = JsonSerializer.Deserialize(json, BootstrapJsonContext.Default.BootstrapConfig)
                     ?? throw new InvalidOperationException($"Failed to parse {configPath} (returned null).");
        }
        else if (options.NonInteractive)
        {
            logger.PhaseFail(Phase, "missing-config-non-interactive");
            throw new InvalidOperationException(
                $"Config file not found at {configPath} and --non-interactive was set. " +
                "Provide --config <path> or rerun interactively.");
        }
        else if (!Console.IsInputRedirected)
        {
            logger.Info($"    no config at {configPath}; entering interactive setup");
            // Pre-prompt mDNS banner: probes {hostname}.local BEFORE we render the prompt so
            // the operator sees the mDNS story up-front. Returns the hostname to seed the
            // "Public host(s)" row with when mDNS works (or the operator opts into installing
            // avahi and the re-probe passes), null otherwise. On null the pre-fill omits the
            // .local name entirely — no point silently pre-filling something that won't
            // resolve on the LAN.
            var mdnsHostname = await ApplyPreFillMdnsCheckAsync(options, logger, ct).ConfigureAwait(false);

            // Real TTY path: mask the OAuth secret prompts so onlookers can't shoulder-surf
            // values being typed. The test path passes maskSecrets:false to avoid Spectre's
            // ReadKey-driven secret path (which complicates TestConsole input pushing).
            config = PromptForConfig(
                AnsiConsole.Console,
                maskSecrets: true,
                hostnameProbe: () => mdnsHostname);
            await PersistAsync(config, configPath, ct).ConfigureAwait(false);
        }
        else
        {
            logger.PhaseFail(Phase, "missing-config-no-tty");
            throw new InvalidOperationException(
                $"Config file not found at {configPath} and no TTY is available. " +
                "Run with --config <path> pointing at a populated interfold.bootstrap.json.");
        }

        Validate(config);

        // Post-fill mDNS safety gate: only for the `bootstrap` subcommand. `publish` /
        // `update-images` / `backup` / `restore` load the persisted JSON verbatim and never
        // mutate hosts — re-running the check there would be misleading noise. If the
        // operator's persisted JSON is stale (e.g. .local names that no longer resolve),
        // they re-run `bootstrap` and pick up the strip + warning path here.
        if (options.Command == BootstrapCommand.Bootstrap)
        {
            var mutated = await ApplyMdnsGateAsync(config, options, logger, ct).ConfigureAwait(false);
            if (mutated)
            {
                // Strip could have emptied the list — re-validate to fail loudly rather than
                // producing a cert with zero SANs later. Matches the "hosts must contain at
                // least one entry" invariant Validate enforces on JSON-loaded configs too.
                Validate(config);
                // Re-persist the config so subsequent `bootstrap` runs see the pruned list
                // and don't re-emit the same warning for the same broken hosts. Applies to
                // both the interactive branch (we just wrote it and it's ours to keep in
                // sync) AND the JSON-load branch (operator's file gets one clean line of
                // "was updated" logging above so the mutation isn't silent). The alternative
                // — never rewriting the operator's JSON — would surface the same "removing
                // unresolvable .local host(s)" warning on every future run indefinitely,
                // which is louder noise than a one-time file touch.
                logger.Info($"    updated {configPath} to reflect the mDNS strip");
                await PersistAsync(config, configPath, ct).ConfigureAwait(false);
            }
        }

        // Align the config's outputDir with the CLI override (if the operator passed --output-dir).
        if (!string.Equals(Path.GetFullPath(config.Deployment.OutputDir), options.OutputDir, StringComparison.Ordinal))
        {
            logger.Info($"    overriding config.outputDir with --output-dir={options.OutputDir}");
            config.Deployment.OutputDir = options.OutputDir;
        }

        logger.PhaseDone(Phase);
        return config;
    }

    /// <summary>
    /// Spectre.Console-driven navigable form for <see cref="BootstrapConfig"/>. Every field is
    /// pre-populated with the schema's property-initialiser default and rendered as a single
    /// menu row showing the current value next to its label; the operator arrow-keys up/down
    /// between rows and presses Enter to edit any field (which re-prompts via the field's
    /// dedicated <see cref="TextPrompt{T}"/> / <see cref="ConfirmationPrompt"/> with inline
    /// validation). Choosing the trailing <c>Confirm and save</c> entry returns the config to
    /// <see cref="RunAsync"/> for validation and persistence. There is no separate "walkthrough"
    /// phase — first-time operators just Enter every row top-to-bottom; experienced operators
    /// jump straight to the rows they care about and skip the rest.
    /// </summary>
    /// <param name="console">
    /// Injected <see cref="IAnsiConsole"/>. Production passes <see cref="AnsiConsole.Console"/>;
    /// unit tests pass a <c>Spectre.Console.Testing.TestConsole</c> seeded with
    /// <c>Input.PushKey(ConsoleKey.DownArrow|Enter)</c> + <c>Input.PushTextWithEnter</c> for
    /// per-field edits, then asserts on the captured <c>Output</c>.
    /// </param>
    /// <param name="maskSecrets">
    /// When true, the three OAuth client-secret prompts use Spectre's <c>Secret()</c> mode so
    /// keystrokes are masked in a real TTY. Defaults to false because Spectre's secret-mode
    /// path is <see cref="Console.ReadKey"/>-driven, which interacts awkwardly with the
    /// <c>TestConsole</c> input queue; the test seam is therefore unmasked. Production callers
    /// in <see cref="RunAsync"/> opt in explicitly.
    /// </param>
    /// <param name="localAddressProbe">
    /// Test seam for the device-IP pre-fill on the <c>Public host(s)</c> row. When the
    /// freshly-constructed <see cref="DeploymentSection.Hosts"/> list is empty (the property's
    /// shipped default) and the probe returns a non-null <see cref="IPAddress"/>, that address
    /// is stored verbatim as the second <c>Hosts</c> entry (after the mDNS hostname when the
    /// hostname probe supplied one). Defaults to
    /// <see cref="LocalAddressDetector.TryDetectPrimaryIp"/> (live NIC enumeration with
    /// loopback / virtual-bridge / link-local filtering); unit tests pass <c>() =&gt; null</c>
    /// to keep the empty-Hosts default deterministic, or a concrete <see cref="IPAddress"/>
    /// fake to exercise the auto-default path. Non-interactive flows (<see cref="RunAsync"/>'s
    /// JSON-load branch + <see cref="Validate"/>) never consult the probe, so the fail-fast
    /// contract on a JSON file with no <c>hosts</c> stays intact.
    /// </param>
    /// <param name="hostnameProbe">
    /// Test seam for the mDNS-hostname pre-fill on the <c>Public host(s)</c> row. When the
    /// freshly-constructed <see cref="DeploymentSection.Hosts"/> list is empty and the probe
    /// returns a non-null string, that value (typically <c>{hostname}.local</c>) is stored
    /// verbatim as the FIRST <c>Hosts</c> entry — landing before the detected IP so the leaf
    /// cert's primary-host derivation (see
    /// <see cref="Phases.ConfigPhase.ResolveDerivedDefaults"/>) latches onto the mDNS name.
    /// <para>
    /// Deliberately defaults to <c>() =&gt; null</c> rather than a live
    /// <see cref="HostnameDetector.TryDetectMdnsHostname"/> call: the "should we pre-fill a
    /// .local name?" decision is owned by <see cref="ApplyPreFillMdnsCheckAsync"/> (which
    /// probes mDNS resolvability, prints a banner, and optionally offers to install avahi
    /// before returning a hostname). Making the default null here means the hostname can
    /// never sneak into the pre-fill on a path that skipped the banner — e.g. a
    /// non-interactive JSON-load run, or a unit test that only asked about IP pre-fill.
    /// Production callers (only <see cref="RunAsync"/>) pass the banner's return value
    /// wrapped in a lambda: <c>hostnameProbe: () =&gt; mdnsHostname</c>.
    /// </para>
    /// </param>
    internal static BootstrapConfig PromptForConfig(
        IAnsiConsole console,
        bool maskSecrets = false,
        Func<IPAddress?>? localAddressProbe = null,
        Func<string?>? hostnameProbe = null)
    {
        var c = new BootstrapConfig();

        // Auto-default the "Public host(s)" row on a fresh interactive bootstrap. The two
        // probes fire only when the shipped-empty Hosts list is untouched — RunAsync's
        // JSON-load branch + Validate stay untouched, so a non-interactive caller passing a
        // config file with empty `hosts` still hard-fails fast (per the explicit contract on
        // DeploymentSection.Hosts). Order matters: hostname first, IP second, so
        // HostParser.PickPrimary latches the mDNS name for the leaf cert / derived URLs when
        // both are present.
        if (c.Deployment.Hosts.Count == 0)
        {
            var seed = new List<string>();
            var hostname = (hostnameProbe ?? (() => null))();
            if (hostname is not null)
            {
                seed.Add(hostname);
            }
            var detected = (localAddressProbe ?? LocalAddressDetector.TryDetectPrimaryIp)();
            if (detected is not null)
            {
                // Store the bare IP literal (e.g. "192.168.1.42" or "fe80::1") — HostParser
                // accepts either form for IPv6 (bracketed or bare) and the bare form is what
                // we want serialised back into the JSON file. Downstream consumers do their
                // own bracketing where required (URL derivation in ResolveDerivedDefaults
                // via HostParser.ToUrlHost) so the SAN encoding stays correct.
                seed.Add(detected.ToString());
            }
            if (seed.Count > 0)
            {
                c.Deployment.Hosts = seed;
            }
        }

        // Local helpers — each closes over `console` (and, for the OAuth wrapper, `maskSecrets`)
        // so the per-field Edit delegates further down can be one-liners. Kept as locals rather
        // than private statics so the captured `console` reference reads naturally; the prompt
        // construction is cheap (no IO until .Show is dispatched by `Prompt`).
        string PromptStr(string label, string fallback) => console.Prompt(
            new TextPrompt<string>($"{label}:")
                .DefaultValue(fallback)
                .AllowEmpty());

        int PromptInt(string label, int fallback, int min, int max) => console.Prompt(
            new TextPrompt<int>($"{label}:")
                .DefaultValue(fallback)
                .ValidationErrorMessage($"[red]must be an integer in [[{min}..{max}]][/]")
                .Validate(n => n >= min && n <= max));

        bool PromptBool(string label, bool fallback) => console.Prompt(
            new ConfirmationPrompt($"{label}?") { DefaultValue = fallback });

        string PromptOAuth(string label, string fallback)
        {
            var p = new TextPrompt<string>($"{label} (blank to skip):")
                .DefaultValue(fallback)
                .AllowEmpty();
            // Apply Secret() conditionally so the test path stays on the plain TextReader-style
            // seam. The production path masks with '*'; null mask would be "secret, but echo
            // nothing" which is correct-but-confusing UX (operator can't see they typed anything).
            if (maskSecrets) p.Secret('*');
            return console.Prompt(p);
        }

        // Nullable-int companion to PromptInt: blank input becomes null (the field's
        // "use the API's compile-time default" signal); a non-blank value still has to
        // be a parseable int in range. The TextPrompt<string> seam keeps the parse logic
        // here rather than relying on Spectre's TextPrompt<int?> (which doesn't exist).
        int? PromptNullableInt(string label, int? fallback, int min, int max)
        {
            var fallbackText = fallback?.ToString() ?? string.Empty;
            var raw = console.Prompt(
                new TextPrompt<string>($"{label} (blank for default):")
                    .DefaultValue(fallbackText)
                    .AllowEmpty()
                    .Validate(s =>
                    {
                        if (string.IsNullOrWhiteSpace(s)) return ValidationResult.Success();
                        if (!int.TryParse(s, out var parsed))
                        {
                            return ValidationResult.Error("[red]must be a whole number or blank[/]");
                        }
                        return parsed >= min && parsed <= max
                            ? ValidationResult.Success()
                            : ValidationResult.Error($"[red]must be in [[{min}..{max}]] or blank[/]");
                    }));
            return string.IsNullOrWhiteSpace(raw) ? null : int.Parse(raw);
        }

        // Field table: every entry is (Label, Show, Edit). Edit runs the per-field Spectre prompt
        // and writes its result back onto `c`. The same table powers both the initial sequential
        // walkthrough and the review-loop's "edit field N" branch — there is one canonical place
        // each field's prompt is defined.
        var fields = new List<(string Label, Func<string> Show, Action Edit)>
        {
            // --- Deployment ---
            ("Output directory",                () => c.Deployment.OutputDir,
                                                () => c.Deployment.OutputDir = PromptStr("Output directory", c.Deployment.OutputDir)),
            ("Public host(s)",                  () => string.Join(",", c.Deployment.Hosts),
                                                () => c.Deployment.Hosts = PromptHosts(console, c.Deployment.Hosts)),
            ("Root CA subject",                 () => c.Deployment.RootCaName,
                                                () => c.Deployment.RootCaName = PromptStr("Root CA subject", c.Deployment.RootCaName)),
            ("Leaf cert validity (years)",      () => c.Deployment.CertYears.ToString(),
                                                () => c.Deployment.CertYears = PromptInt("Leaf cert validity (years)", c.Deployment.CertYears, 1, 30)),
            ("Install root CA in trust store",  () => c.Deployment.TrustStoreInstall.ToString(),
                                                () => c.Deployment.TrustStoreInstall = PromptBool("Install root CA into system trust store", c.Deployment.TrustStoreInstall)),
            // Independent from the TLS toggle below: operators can ship octocon-web HTTP-only
            // (useful for local debugging and stacks fronted by an external TLS proxy). The
            // publish wiring auto-promotes this to true when webHttps=true, so leaving it at
            // its default false alongside webHttps=true still ships the container — the
            // implicit promotion mirrors the long-standing behaviour.
            ("Include octocon-web container",   () => c.Deployment.IncludeWeb.ToString(),
                                                () => c.Deployment.IncludeWeb = PromptBool("Include the octocon-web (Kotlin/Wasm UI) container", c.Deployment.IncludeWeb)),
            ("Terminate HTTPS at octocon-web",  () => c.Deployment.WebHttps.ToString(),
                                                () => c.Deployment.WebHttps = PromptBool("Terminate HTTPS at octocon-web", c.Deployment.WebHttps)),

            // --- Ports ---
            ("API HTTP port",                   () => c.Ports.ApiHttp.ToString(),
                                                () => c.Ports.ApiHttp = PromptInt("API HTTP port", c.Ports.ApiHttp, 1, 65535)),
            ("API HTTPS port",                  () => c.Ports.ApiHttps.ToString(),
                                                () => c.Ports.ApiHttps = PromptInt("API HTTPS port", c.Ports.ApiHttps, 1, 65535)),
            ("Web HTTP port",                   () => c.Ports.WebHttp.ToString(),
                                                () => c.Ports.WebHttp = PromptInt("Web HTTP port", c.Ports.WebHttp, 1, 65535)),
            ("Web HTTPS port",                  () => c.Ports.WebHttps.ToString(),
                                                () => c.Ports.WebHttps = PromptInt("Web HTTPS port", c.Ports.WebHttps, 1, 65535)),
            ("Postgres host port",              () => c.Ports.Postgres.ToString(),
                                                () => c.Ports.Postgres = PromptInt("Postgres host port", c.Ports.Postgres, 1, 65535)),
            ("Scylla/Cassandra host port",      () => c.Ports.Scylla.ToString(),
                                                () => c.Ports.Scylla = PromptInt("Scylla/Cassandra host port", c.Ports.Scylla, 1, 65535)),

            // --- Database ---
            ("Database mode",                   () => c.DatabaseMode,
                                                () => c.DatabaseMode = console.Prompt(
                                                    new TextPrompt<string>("Database mode:")
                                                        .DefaultValue(c.DatabaseMode)
                                                        .AddChoices(new[] { "single", "multi", "cassandra" }))),
            ("Postgres application DB name",    () => c.PostgresDatabase,
                                                () => c.PostgresDatabase = PromptStr("Postgres application DB name", c.PostgresDatabase)),
            ("Cluster name",                    () => c.ClusterName,
                                                () => c.ClusterName = PromptStr("Cluster name (Scylla/Cassandra)", c.ClusterName)),
            // Scylla keyspace == per-instance region identity. Constrained to the seven valid
            // values via AddChoices so the operator can't typo themselves into a runtime
            // "wrong region" failure; Validate enforces the same list non-interactively.
            ("Scylla keyspace (region)",        () => c.ScyllaKeyspace,
                                                () => c.ScyllaKeyspace = console.Prompt(
                                                    new TextPrompt<string>("Scylla keyspace (region):")
                                                        .DefaultValue(c.ScyllaKeyspace)
                                                        .AddChoices(ValidScyllaKeyspaces))),

            // --- API ---
            // Show callbacks for the three derivable rows call ResolveDerivedDefaults via a
            // local snapshot so the menu paints the computed default next to its label even
            // before the operator presses Enter. Editing then offers that same derived value
            // as the prompt's default — operators get a one-keypress accept-the-default flow.
            ("OAuth callback base URL",         () => DerivedShow(c, ar => ar.CallbackBaseUrl),
                                                () => c.ApiRuntime.CallbackBaseUrl = PromptStr(
                                                    "OAuth callback base URL",
                                                    DerivedShow(c, ar => ar.CallbackBaseUrl))),
            ("JWT authority (iss claim)",       () => DerivedShow(c, ar => ar.JwtAuthority),
                                                () => c.ApiRuntime.JwtAuthority = PromptStr(
                                                    "JWT authority (iss claim)",
                                                    DerivedShow(c, ar => ar.JwtAuthority))),
            ("JWT audience (aud claim)",        () => c.ApiRuntime.JwtAudience,
                                                () => c.ApiRuntime.JwtAudience = PromptStr(
                                                    "JWT audience (aud claim)", c.ApiRuntime.JwtAudience)),
            ("CORS allowed origins",            () => string.Join(",", DerivedCorsShow(c)),
                                                () => c.ApiRuntime.CorsAllowedOrigins = PromptCorsAllowedOrigins(
                                                    console, DerivedCorsShow(c))),
            ("Pre-built Interfold API image",   () => c.ApiImage,
                                                () => c.ApiImage = PromptStr("Pre-built Interfold API image reference", c.ApiImage)),

            // --- Cluster & telemetry ---
            // NodeGroup uses AddChoices to enforce the three valid values upfront. Non-Fly
            // self-hosters usually want "primary"; the default stays "auxiliary" to match
            // the API's compile-time fallback so a brand-new bootstrap doesn't silently
            // change behaviour for stacks that didn't have a value before.
            ("Cluster node group",              () => c.Cluster.NodeGroup,
                                                () => c.Cluster.NodeGroup = console.Prompt(
                                                    new TextPrompt<string>("Cluster node group:")
                                                        .DefaultValue(c.Cluster.NodeGroup)
                                                        .AddChoices(ValidNodeGroups))),
            // OTLP endpoint is optional — blank disables OTLP entirely. ShowOrEmpty makes the
            // unset state visible in the menu (same UX as the OAuth client IDs).
            ("OTLP endpoint",                   () => ShowOrEmpty(c.Observability.OtlpEndpoint),
                                                () => c.Observability.OtlpEndpoint = PromptStr(
                                                    "OTLP endpoint (blank to disable)",
                                                    c.Observability.OtlpEndpoint)),

            // --- Storage ---
            // Both rows are optional; their blank-state behaviour is "AppHost-managed
            // defaults" rather than "disabled":
            //
            //   AvatarStorageRoot blank → the AppHost substitutes /app/data/avatars and
            //   mounts an `interfold_avatars` named Docker volume there so the bytes
            //   survive container restarts (with the right `app:app` ownership the
            //   published image pre-creates the directory with). Non-blank means the
            //   operator owns the mount: they're responsible for the bind/volume target,
            //   the host-side path, AND the permissions that let the API process (running
            //   as user `app`, UID 1654 — the .NET SDK container default) read+write
            //   inside it. The AppHost will NOT silently add a named volume for paths it
            //   doesn't control.
            //
            //   AvatarPublicBase blank → the API serves /avatars/* directly (matches the
            //   LocalAvatarStorage URL stamp default). Non-blank with a path-only value
            //   (`/static/avatars`) re-prefixes the served path. Non-blank with an
            //   absolute https URL hands ownership of the byte-serving surface to a CDN
            //   / reverse proxy and the API stops trying to serve the files itself (see
            //   AvatarServingPolicy.Resolve in the API project).
            ("Avatar storage root (container path)", () => ShowOrEmpty(c.Storage.AvatarStorageRoot),
                                                () => c.Storage.AvatarStorageRoot = PromptStr(
                                                    "Avatar storage root (blank = /app/data/avatars, persisted by AppHost-managed volume; non-blank = you manage the mount and ownership)",
                                                    c.Storage.AvatarStorageRoot)),
            ("Avatar public base URL",          () => ShowOrEmpty(c.Storage.AvatarPublicBase),
                                                () => c.Storage.AvatarPublicBase = PromptStr(
                                                    "Avatar public base URL (blank = API serves /avatars/* directly; set https URL to delegate to CDN)",
                                                    c.Storage.AvatarPublicBase)),

            // --- Performance tuning ---
            // BatchBytesThreshold is nullable — PromptNullableInt treats blank as null (the
            // "use the API's compile-time default" signal). Show callback renders "<default>"
            // when null so the unset state is visible.
            ("Socket batch flush threshold (bytes)",
                                                () => c.Socket.BatchBytesThreshold?.ToString() ?? "<default>",
                                                () => c.Socket.BatchBytesThreshold = PromptNullableInt(
                                                    "Socket batch flush threshold (bytes)",
                                                    c.Socket.BatchBytesThreshold, 1, 16 * 1024 * 1024)),
            ("DB retry attempts",               () => c.Persistence.DbRetryAttempts.ToString(),
                                                () => c.Persistence.DbRetryAttempts = PromptInt(
                                                    "DB retry attempts", c.Persistence.DbRetryAttempts, 1, 100)),
            ("DB retry initial delay (ms)",     () => c.Persistence.DbRetryInitialDelayMs.ToString(),
                                                () => c.Persistence.DbRetryInitialDelayMs = PromptInt(
                                                    "DB retry initial delay (ms)",
                                                    c.Persistence.DbRetryInitialDelayMs, 1, 60_000)),
            ("DB retry max delay (ms)",         () => c.Persistence.DbRetryMaxDelayMs.ToString(),
                                                () => c.Persistence.DbRetryMaxDelayMs = PromptInt(
                                                    "DB retry max delay (ms)",
                                                    c.Persistence.DbRetryMaxDelayMs, 1, 600_000)),
            ("Hydration max concurrency",       () => c.Persistence.HydrationMaxConcurrency.ToString(),
                                                () => c.Persistence.HydrationMaxConcurrency = PromptInt(
                                                    "Hydration max concurrency",
                                                    c.Persistence.HydrationMaxConcurrency, 1, 1024)),

            // --- OAuth credentials ---
            // Rows are paired per provider (ID then secret) so the operator can fill both halves
            // of a single provider's credentials in sequence before moving on. Client IDs are
            // public values (rendered into each provider's authorize redirect URL), so they go
            // through plain PromptStr — no masking on the prompt and the value renders verbatim
            // on the menu row when set. When the ID is empty, ShowOrEmpty paints the same
            // `<empty>` marker the secret rows use, so the operator can tell at a glance that
            // the row exists and is unset (vs. just a blank cell, which reads as "no row").
            // Client secrets keep their masked PromptOAuth path and the Mask() <set>/<empty>
            // display rule.
            ("Google OAuth client ID",          () => ShowOrEmpty(c.OAuth.GoogleClientId),
                                                () => c.OAuth.GoogleClientId = PromptStr("Google OAuth client ID", c.OAuth.GoogleClientId)),
            ("Google OAuth client secret",      () => Mask(c.OAuth.GoogleClientSecret),
                                                () => c.OAuth.GoogleClientSecret = PromptOAuth("Google OAuth client secret", c.OAuth.GoogleClientSecret)),
            ("Discord OAuth client ID",         () => ShowOrEmpty(c.OAuth.DiscordClientId),
                                                () => c.OAuth.DiscordClientId = PromptStr("Discord OAuth client ID", c.OAuth.DiscordClientId)),
            ("Discord OAuth client secret",     () => Mask(c.OAuth.DiscordClientSecret),
                                                () => c.OAuth.DiscordClientSecret = PromptOAuth("Discord OAuth client secret", c.OAuth.DiscordClientSecret)),
            ("Apple OAuth client ID",           () => ShowOrEmpty(c.OAuth.AppleClientId),
                                                () => c.OAuth.AppleClientId = PromptStr("Apple OAuth client ID", c.OAuth.AppleClientId)),
            ("Apple OAuth client secret",       () => Mask(c.OAuth.AppleClientSecret),
                                                () => c.OAuth.AppleClientSecret = PromptOAuth("Apple OAuth client secret", c.OAuth.AppleClientSecret)),

            // --- Backup & autostart ---
            // Master toggle for the scheduled-backup feature. When false the install-service
            // subcommand still installs the unit files but does NOT enable
            // interfold-backup.timer. The one-shot `backup` subcommand works regardless.
            ("Scheduled backups enabled",       () => c.Backup.Enabled.ToString(),
                                                () => c.Backup.Enabled = PromptBool("Enable scheduled backups (systemd timer)", c.Backup.Enabled)),
            ("Backup schedule (OnCalendar)",    () => c.Backup.Schedule,
                                                () => c.Backup.Schedule = PromptStr("Backup schedule (systemd OnCalendar, e.g. 'daily', 'weekly', 'Mon..Fri 03:30')", c.Backup.Schedule)),
            ("Backup retention (count per component)", () => c.Backup.RetainCount.ToString(),
                                                () => c.Backup.RetainCount = PromptInt("Backup retention (number of archives to keep per component)", c.Backup.RetainCount, 1, 1000)),
            // Blank = "{outputDir}/backups". Non-blank must be absolute (systemd timer
            // invocations have an unpredictable CWD, so relative paths would silently
            // resolve against / or wherever the unit happens to land).
            ("Backup directory (absolute, blank=default)", () => ShowOrEmpty(c.Backup.Directory),
                                                () => c.Backup.Directory = PromptStr("Backup directory (absolute path; leave blank to default to {outputDir}/backups)", c.Backup.Directory)),
            ("Autostart server on boot",        () => c.Backup.AutostartServer.ToString(),
                                                () => c.Backup.AutostartServer = PromptBool("Autostart the server on host boot (installs interfold.service)", c.Backup.AutostartServer)),

            // --- Updates ---
            // Chain interfold-update.service after interfold-backup.service via a
            // systemd OnSuccess= drop-in. Off by default: manual `update-images`
            // works regardless.
            ("Chain updates after backup",      () => c.Update.Enabled.ToString(),
                                                () => c.Update.Enabled = PromptBool("Chain interfold-update.service after each successful backup", c.Update.Enabled)),
            ("Health-check timeout (seconds)",  () => c.Update.HealthCheckTimeoutSeconds.ToString(),
                                                () => c.Update.HealthCheckTimeoutSeconds = PromptInt("Health-check timeout after pull+recreate (seconds)", c.Update.HealthCheckTimeoutSeconds, 1, 3600)),
            ("Auto-restore on failure",         () => c.Update.AutoRestoreOnFailure.ToString(),
                                                () => c.Update.AutoRestoreOnFailure = PromptBool("Auto-restore the pre-update backup on health-check failure (destructive)", c.Update.AutoRestoreOnFailure)),
            ("Recreate containers on update",   () => c.Update.RecreateOnUpdate.ToString(),
                                                () => c.Update.RecreateOnUpdate = PromptBool("Recreate containers on update (uses 'up -d'; disable only for staged pulls)", c.Update.RecreateOnUpdate)),
            // Services whitelist. Blank = "every service"; non-empty is a
            // comma-separated list validated against ValidUpdateServices.
            ("Update service whitelist (blank=all)", () => c.Update.Services.Length == 0 ? "<all>" : string.Join(",", c.Update.Services),
                                                () => c.Update.Services = PromptUpdateServices(console, c.Update.Services)),

            // --- Firebase ---
            // Single row that opens a three-way wizard (auto-detect from folder / configure
            // per-file / clear all). The stored state is the four *Path fields on
            // FirebaseSection; this row's Show summarises how many of them are set so the
            // operator can see the section's state without opening the wizard. See
            // PromptFirebase / FirebaseFolderScanner for the wizard's dispatch.
            ("Firebase push notifications",     () => ShowFirebaseState(c.Firebase),
                                                () => PromptFirebase(console, c.Firebase)),
        };

        // Section boundaries for the menu's grouped layout. Each row is (0-based index of the
        // first field in the section, header label). Kept as a sibling table rather than baked
        // into `fields` so the field list stays a flat sequence — every menu row's value is a
        // direct index into `fields`, with section headers rendered above each group as inert
        // children of an AddChoiceGroup call (selection skips them automatically).
        var sections = new (int FirstFieldIndex, string Header)[]
        {
            (0,  "Deployment"),
            (6,  "Ports"),
            // Database now owns 4 rows (mode, postgresDb, clusterName, scyllaKeyspace).
            (12, "Database"),
            // API section absorbs the old single-row "API image" section plus the four
            // ApiRuntime fields. Layout: callback URL, JWT authority, JWT audience, CORS,
            // then the API image reference at the bottom (matches the JSON file's tail-end
            // position for apiImage and keeps the auth/security-shaped rows together).
            (16, "API"),
            // Cluster (1: NodeGroup) + Observability (1: OTLP) — both are "where this
            // instance fits in the wider system" and individually too small to warrant
            // their own section header.
            (21, "Cluster & telemetry"),
            (23, "Storage"),
            // Socket (1) + Persistence tuning (4) merged into a single tuning section
            // — heterogeneous fields but all "knobs the operator probably leaves alone".
            (25, "Performance tuning"),
            (30, "OAuth credentials"),
            // Backup + autostart section sits after the credentials block because
            // most operators leave its five fields at their no-op defaults.
            (36, "Backup & autostart"),
            // Updates section: five new rows under the backup group; the same
            // reasoning applies (opt-in feature, low interaction rate).
            (41, "Updates"),
            // Firebase: single opt-in row whose Edit opens the auto-detect / per-file
            // wizard. Kept in its own trailing section so the row's four-file surface
            // reads as a distinct feature rather than "one more Update knob".
            (47, "Firebase"),
        };

        // Sentinels: -1 = "Confirm and save" (commits the form and returns). The 0..fields.Count-1
        // range is the selectable field-index value the form returns when the operator picks a row;
        // -(2 + sectionIdx) is the inert group-header value that AddChoiceGroup needs (unique per
        // section so the choice set has no duplicates). The converter renders all three classes —
        // confirm, field row, header — into their user-facing label.
        const int ConfirmSentinel = -1;
        int SectionHeaderSentinel(int sectionIdx) => -(2 + sectionIdx);
        int SectionLength(int sectionIdx) =>
            (sectionIdx + 1 < sections.Length ? sections[sectionIdx + 1].FirstFieldIndex : fields.Count)
            - sections[sectionIdx].FirstFieldIndex;

        // Navigable form loop. Re-runs the prompt after every edit so the menu re-renders with
        // the freshly-typed value visible next to its field label; the form exits only when the
        // operator selects "Confirm and save".
        while (true)
        {
            var prompt = new SelectionPrompt<int>()
                .Title(
                    "[bold]Configure interfold.bootstrap.json[/]\n" +
                    "[grey]Use arrow keys to navigate, Enter to edit, choose [green]Confirm and save[/] when done.[/]")
                // Default Spectre page size is 10; the form has 48 fields + 11 headers + 1 confirm
                // = 60 rows. Sizing the page to fit them all means no scrolling on a 60+ row
                // terminal; smaller TTYs still paginate cleanly with the MoreChoicesText hint
                // below. Operators on a 24-row TTY will scroll but every row is reachable.
                .PageSize(60)
                .MoreChoicesText("[grey](move up/down to reveal more)[/]")
                // Markup.Escape on the value because some fields legitimately contain markup-like
                // characters (e.g. ApiImage's `ghcr.io/...:latest` is safe but a future operator
                // value with `[` would otherwise blow up Spectre's markup parser).
                .UseConverter(i =>
                {
                    if (i == ConfirmSentinel) return "[green]Confirm and save[/]";
                    if (i < 0)
                    {
                        var sectionIdx = -(i + 2);
                        return $"[bold]--- {sections[sectionIdx].Header} ---[/]";
                    }
                    var label = fields[i].Label.PadRight(40);
                    var value = Markup.Escape(fields[i].Show());
                    return $"{label} [grey]{value}[/]";
                });

            // AddChoiceGroup renders the group key (here: the inert header sentinel) as a
            // non-selectable label above its children, so arrow-key navigation moves only
            // between the selectable field rows under each header.
            for (var s = 0; s < sections.Length; s++)
            {
                var firstIdx = sections[s].FirstFieldIndex;
                var sectionFieldIdx = Enumerable.Range(firstIdx, SectionLength(s)).ToArray();
                prompt.AddChoiceGroup(SectionHeaderSentinel(s), sectionFieldIdx);
            }
            prompt.AddChoices(ConfirmSentinel);

            var selected = console.Prompt(prompt);
            if (selected == ConfirmSentinel) break;
            fields[selected].Edit();
        }

        return c;
    }

    /// <summary>
    /// Hosts-only prompt. Most other fields are single-value; this one parses a comma-
    /// separated string into a list and validates inline that every surviving entry is a
    /// valid host (DNS name, IPv4 / IPv6 literal, or CIDR block). Lives outside
    /// <see cref="PromptForConfig"/>'s local helpers because the parse-into-list shape
    /// doesn't generalise to a one-liner. The CORS allow-list uses the same parse shape —
    /// see <see cref="PromptCorsAllowedOrigins"/>.
    /// </summary>
    private static List<string> PromptHosts(IAnsiConsole console, List<string> fallback)
    {
        var fallbackText = string.Join(",", fallback);
        var raw = console.Prompt(
            new TextPrompt<string>("Public host(s) (domain, IP, or CIDR), comma separated:")
                .DefaultValue(fallbackText)
                .AllowEmpty()
                .Validate(s =>
                {
                    var trimmed = s?.Trim() ?? string.Empty;
                    if (string.IsNullOrEmpty(trimmed))
                    {
                        // Empty input only OK when the fallback was non-empty (operator editing
                        // an existing config and re-confirming). On a fresh bootstrap the
                        // fallback is [] and we must force the operator to type something so
                        // we never silently issue a cert with no SANs.
                        return fallback.Count == 0
                            ? ValidationResult.Error("[red]at least one host required (no default to fall back on)[/]")
                            : ValidationResult.Success();
                    }
                    var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length == 0)
                    {
                        return ValidationResult.Error("[red]at least one host required[/]");
                    }
                    foreach (var part in parts)
                    {
                        try
                        {
                            HostParser.Parse(part);
                        }
                        catch (FormatException ex)
                        {
                            return ValidationResult.Error($"[red]{ex.Message}[/]");
                        }
                    }
                    return ValidationResult.Success();
                }));

        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /// <summary>
    /// CORS allow-list prompt. Twin of <see cref="PromptHosts"/> for the
    /// <see cref="ApiRuntimeSection.CorsAllowedOrigins"/> field — same comma-separated parse
    /// shape, but every entry is validated inline as an absolute http(s) URI to match the
    /// non-interactive <see cref="Validate"/> rule. Pre-fills the prompt with the derived
    /// fallback (one entry per non-CIDR <see cref="DeploymentSection.Hosts"/> entry) so the
    /// operator gets a sensible Enter-to-accept default even on first run.
    /// </summary>
    private static List<string> PromptCorsAllowedOrigins(IAnsiConsole console, List<string> fallback)
    {
        var fallbackText = string.Join(",", fallback);
        var raw = console.Prompt(
            new TextPrompt<string>("CORS allowed origins, comma separated:")
                .DefaultValue(fallbackText)
                .AllowEmpty()
                .Validate(s =>
                {
                    if (string.IsNullOrWhiteSpace(s)) return ValidationResult.Success();
                    var parts = s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length == 0)
                    {
                        return ValidationResult.Error("[red]at least one origin required[/]");
                    }
                    foreach (var part in parts)
                    {
                        if (!Uri.TryCreate(part, UriKind.Absolute, out var uri)
                            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                        {
                            return ValidationResult.Error($"[red]'{part}' is not a valid http(s) origin[/]");
                        }
                    }
                    return ValidationResult.Success();
                }));

        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /// <summary>
    /// Prompt for the <see cref="UpdateSection.Services"/> whitelist. Blank input clears
    /// the list back to "every service" (the shipped default); a comma-separated list is
    /// validated inline against <see cref="ValidUpdateServices"/> so a typo (e.g.
    /// <c>msg-database</c>) is caught before the operator saves the form, not later
    /// when <c>docker compose pull</c> reports "no such service".
    /// </summary>
    private static string[] PromptUpdateServices(IAnsiConsole console, string[] fallback)
    {
        var fallbackText = string.Join(",", fallback);
        var raw = console.Prompt(
            new TextPrompt<string>(
                "Update service whitelist (comma-separated; blank = every service):")
                .DefaultValue(fallbackText)
                .AllowEmpty()
                .Validate(s =>
                {
                    if (string.IsNullOrWhiteSpace(s)) return ValidationResult.Success();
                    var parts = s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var part in parts)
                    {
                        if (!ValidUpdateServices.Contains(part, StringComparer.Ordinal))
                        {
                            return ValidationResult.Error(
                                $"[red]'{part}' is not a known compose service. Expected one of: {string.Join(", ", ValidUpdateServices)}[/]");
                        }
                    }
                    return ValidationResult.Success();
                }));

        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToArray();
    }

    /// <summary>
    /// Menu-row summary for the Firebase wizard. Renders <c>off</c> when none of the four
    /// <see cref="FirebaseSection"/> paths are set, or <c>N/4 configured (…)</c> listing
    /// the platform labels that are, so the operator can see the section's state without
    /// opening the wizard.
    /// </summary>
    private static string ShowFirebaseState(FirebaseSection section)
    {
        var platforms = new List<string>(4);
        if (!string.IsNullOrEmpty(section.AndroidConfigPath)) platforms.Add("android");
        if (!string.IsNullOrEmpty(section.IosConfigPath)) platforms.Add("ios");
        if (!string.IsNullOrEmpty(section.WebConfigPath)) platforms.Add("web");
        if (!string.IsNullOrEmpty(section.ServiceAccountPath)) platforms.Add("service_account");
        return platforms.Count == 0
            ? "off"
            : $"{platforms.Count}/4 configured ({string.Join(", ", platforms)})";
    }

    // Wizard branch labels — kept as constants because both the SelectionPrompt below and
    // the ConfigInteractivePromptTests navigation helpers reference them by exact string,
    // and letting the two drift silently past each other would break the interactive test
    // seam without a compile-time signal.
    private const string FirebaseChoiceAutoDetect = "Auto-detect from folder";
    private const string FirebaseChoicePerFile = "Configure per-file";
    private const string FirebaseChoiceClear = "Clear all";
    private const string FirebaseChoiceCancel = "Cancel";

    /// <summary>
    /// Three-way (plus Cancel) wizard fired from the Firebase menu row. Delegates path
    /// discovery to <see cref="FirebaseFolderScanner"/> for the folder branch and falls
    /// back to four sequential <see cref="TextPrompt{T}"/> calls for the per-file branch;
    /// the clear branch zeros the four paths, and cancel is a no-op. All state lives on
    /// the shared <paramref name="section"/> so the menu redraws with the updated
    /// <see cref="ShowFirebaseState"/> summary as soon as the wizard returns.
    /// </summary>
    private static void PromptFirebase(IAnsiConsole console, FirebaseSection section)
    {
        var choice = console.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Firebase push notifications[/]")
                .AddChoices(FirebaseChoiceAutoDetect, FirebaseChoicePerFile, FirebaseChoiceClear, FirebaseChoiceCancel));

        switch (choice)
        {
            case FirebaseChoiceAutoDetect:
                PromptFirebaseFolder(console, section);
                break;

            case FirebaseChoicePerFile:
                section.AndroidConfigPath = PromptFirebasePath(console,
                    "Path to google-services.json", section.AndroidConfigPath);
                section.IosConfigPath = PromptFirebasePath(console,
                    "Path to GoogleService-Info.plist", section.IosConfigPath);
                section.WebConfigPath = PromptFirebasePath(console,
                    "Path to firebase-web-config.json", section.WebConfigPath);
                section.ServiceAccountPath = PromptFirebasePath(console,
                    "Path to FCM v1 service-account JSON", section.ServiceAccountPath);
                break;

            case FirebaseChoiceClear:
                section.AndroidConfigPath = string.Empty;
                section.IosConfigPath = string.Empty;
                section.WebConfigPath = string.Empty;
                section.ServiceAccountPath = string.Empty;
                break;
        }
    }

    /// <summary>
    /// Folder-input branch of the Firebase wizard. Prompts for a directory, hands it to
    /// <see cref="FirebaseFolderScanner.Scan"/>, writes the four resolved paths back onto
    /// <paramref name="section"/>, and prints a two-column summary of what was found vs.
    /// missing. Blank input aborts silently — matches the "Cancel" branch semantics.
    /// </summary>
    private static void PromptFirebaseFolder(IAnsiConsole console, FirebaseSection section)
    {
        var folder = console.Prompt(
            new TextPrompt<string>("Folder containing Firebase files (blank to cancel):")
                .AllowEmpty()
                .Validate(s =>
                {
                    if (string.IsNullOrWhiteSpace(s)) return ValidationResult.Success();
                    return Directory.Exists(s)
                        ? ValidationResult.Success()
                        : ValidationResult.Error($"[red]'{s}' is not an existing directory[/]");
                }));
        if (string.IsNullOrWhiteSpace(folder)) return;

        var result = FirebaseFolderScanner.Scan(folder);
        section.AndroidConfigPath = result.Section.AndroidConfigPath;
        section.IosConfigPath = result.Section.IosConfigPath;
        section.WebConfigPath = result.Section.WebConfigPath;
        section.ServiceAccountPath = result.Section.ServiceAccountPath;

        RenderFirebaseScanSummary(console, result);
    }

    /// <summary>
    /// Per-file prompt used by <see cref="PromptFirebase"/>'s per-file branch. Blank input
    /// returns empty so the operator can leave any single platform unwired; a non-blank
    /// value must resolve to an existing file (a typo shouldn't silently disable push
    /// for that platform — the operator can still commit an empty path when they mean it).
    /// </summary>
    private static string PromptFirebasePath(IAnsiConsole console, string label, string fallback) =>
        console.Prompt(
            new TextPrompt<string>($"{label} (blank to skip):")
                .DefaultValue(fallback)
                .AllowEmpty()
                .Validate(s =>
                {
                    if (string.IsNullOrWhiteSpace(s)) return ValidationResult.Success();
                    return File.Exists(s)
                        ? ValidationResult.Success()
                        : ValidationResult.Error($"[red]'{s}' does not exist[/]");
                }));

    /// <summary>
    /// Renders the auto-detect outcome as a Spectre table so the operator sees at a glance
    /// which platforms were resolved and which are still missing (and would need the
    /// per-file path branch, or an explicit clear-all, to finish configuring).
    /// </summary>
    private static void RenderFirebaseScanSummary(IAnsiConsole console, FirebaseFolderScanResult result)
    {
        var table = new Table()
            .AddColumn("Platform")
            .AddColumn("Resolved path");

        AddScanRow(table, "android",         result.Section.AndroidConfigPath);
        AddScanRow(table, "ios",             result.Section.IosConfigPath);
        AddScanRow(table, "web",             result.Section.WebConfigPath);
        AddScanRow(table, "service_account", result.Section.ServiceAccountPath);

        console.Write(table);
        if (result.Missing.Count > 0)
        {
            console.MarkupLine(
                $"[yellow]Missing: {string.Join(", ", result.Missing)}. " +
                "Re-open the wizard and choose 'Configure per-file' to point at them individually.[/]");
        }
    }

    private static void AddScanRow(Table table, string platform, string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            table.AddRow($"[grey]{platform}[/]", "[grey]-[/]");
        }
        else
        {
            table.AddRow(platform, Markup.Escape(path));
        }
    }

    /// <summary>
    /// Show-callback helper for the two derivable <see cref="ApiRuntimeSection"/> single-value
    /// fields (<see cref="ApiRuntimeSection.CallbackBaseUrl"/> / <see cref="ApiRuntimeSection.JwtAuthority"/>).
    /// Runs <see cref="ResolveDerivedDefaults"/> on a working copy of the
    /// <see cref="ApiRuntimeSection"/> so the menu row paints the derived value when the stored
    /// field is empty, but the operator's stored value still wins when it's non-empty. Reads
    /// the field via a selector so the same plumbing serves both rows.
    /// </summary>
    private static string DerivedShow(BootstrapConfig c, Func<ApiRuntimeSection, string> selector)
    {
        var snapshot = CloneForDerivation(c);
        ResolveDerivedDefaults(snapshot);
        return selector(snapshot.ApiRuntime);
    }

    /// <summary>
    /// CORS-row sibling of <see cref="DerivedShow"/>. The single-value derivation helper isn't
    /// reusable for the list-shaped CORS field, so it gets its own one-liner. The returned
    /// list is what both the menu row's display string and the Edit prompt's fallback consume.
    /// </summary>
    private static List<string> DerivedCorsShow(BootstrapConfig c)
    {
        var snapshot = CloneForDerivation(c);
        ResolveDerivedDefaults(snapshot);
        return snapshot.ApiRuntime.CorsAllowedOrigins;
    }

    /// <summary>
    /// Builds a transient <see cref="BootstrapConfig"/> with just the fields
    /// <see cref="ResolveDerivedDefaults"/> consumes (deployment) and writes to (apiRuntime).
    /// The snapshot is throwaway: <see cref="DerivedShow"/> / <see cref="DerivedCorsShow"/>
    /// use it to compute derived values without mutating the live config the menu shares with
    /// the Edit callbacks. Cheap — only allocates one config + one section instance per redraw.
    /// </summary>
    private static BootstrapConfig CloneForDerivation(BootstrapConfig c)
    {
        return new BootstrapConfig
        {
            Deployment = new DeploymentSection
            {
                Hosts = c.Deployment.Hosts,
                WebHttps = c.Deployment.WebHttps,
            },
            ApiRuntime = new ApiRuntimeSection
            {
                CallbackBaseUrl = c.ApiRuntime.CallbackBaseUrl,
                JwtAuthority = c.ApiRuntime.JwtAuthority,
                JwtAudience = c.ApiRuntime.JwtAudience,
                CorsAllowedOrigins = [.. c.ApiRuntime.CorsAllowedOrigins],
            },
        };
    }

    /// <summary>
    /// Display rule for the OAuth-secret menu rows: present-or-not without echoing the raw
    /// value. The selection form re-renders every iteration of the navigable loop (including
    /// right after an OAuth secret edit), so this is the single guard against leaking the
    /// typed secret back onto the menu — supplements the per-field <c>Secret('*')</c> masking
    /// that protects the prompt-input echo itself in a real TTY.
    /// </summary>
    private static string Mask(string secret) =>
        string.IsNullOrEmpty(secret) ? "<empty>" : "<set>";

    /// <summary>
    /// Display rule for the OAuth-client-ID menu rows (and any other plain-text row whose
    /// value can legitimately be blank). Renders the value verbatim when set, falling back to
    /// the same <c>&lt;empty&gt;</c> marker the secret rows use when unset — so the operator
    /// can tell at a glance that the row exists and is unset (vs. a blank cell, which reads
    /// as "no row at all"). Unlike <see cref="Mask"/>, the actual value is shown when
    /// non-empty: client IDs are public per-provider identifiers and end up in the OAuth
    /// redirect URL anyway, so there's no value in hiding them.
    /// </summary>
    private static string ShowOrEmpty(string value) =>
        string.IsNullOrEmpty(value) ? "<empty>" : value;

    /// <summary>
    /// The seven regional keyspace identifiers the API recognises. Must stay aligned with the
    /// region list in <c>InterfoldAppHost.Configure</c> (the multi-mode Scylla node loop) and
    /// the matching list in <see cref="PublishPhase.BuildEnvReplacements"/>. Order matches the
    /// canonical order used elsewhere in the codebase for stable diffs.
    /// </summary>
    internal static readonly string[] ValidScyllaKeyspaces =
        ["nam", "eur", "sam", "sas", "eas", "ocn", "gdpr"];

    /// <summary>
    /// The three node roles <see cref="Interfold.Contracts.Configuration.ClusterConfiguration"/>
    /// recognises. The API's <c>ApplyCluster</c> lower-cases incoming values then assigns them
    /// to <c>NodeGroup</c>; downstream code branches on these exact tokens, so the validator
    /// rejects anything outside the set upfront rather than letting a typo silently degrade to
    /// the <c>auxiliary</c> default at runtime.
    /// </summary>
    internal static readonly string[] ValidNodeGroups = ["primary", "auxiliary", "sidecar"];

    /// <summary>
    /// Whitelist of compose service names <see cref="UpdateSection.Services"/> may reference.
    /// Must stay aligned with the <c>builder.AddContainer(...)</c> names in
    /// <c>InterfoldAppHost.Configure</c>. Multi-region Scylla adds one service per region
    /// (<c>scylla-nam</c>, <c>scylla-eur</c>, ...); the seven regional identifiers here match
    /// <see cref="ValidScyllaKeyspaces"/> so an operator restricting the update whitelist to
    /// "just the API tier" or "just one Scylla region" doesn't have to memorise a second list.
    /// The validator surfaces the canonical set in its error message so a typo is easy to fix.
    /// </summary>
    internal static readonly string[] ValidUpdateServices =
    [
        "msg-db",
        "scylla",
        "scylla-nam", "scylla-eur", "scylla-sam", "scylla-sas",
        "scylla-eas", "scylla-ocn", "scylla-gdpr",
        "cassandra",
        "interfold-api",
        "octocon-web",
    ];

    /// <summary>The default port for the <c>http</c> URI scheme (RFC 7230 §2.7.1).</summary>
    private const int DefaultHttpPort = 80;

    /// <summary>The default port for the <c>https</c> URI scheme (RFC 7230 §2.7.2).</summary>
    private const int DefaultHttpsPort = 443;

    /// <summary>
    /// Fills any empty <see cref="ApiRuntimeSection"/> field that has a derivable default from
    /// <see cref="DeploymentSection"/> + <see cref="PortsSection"/>.
    /// <list type="bullet">
    ///   <item>
    ///     <see cref="ApiRuntimeSection.CallbackBaseUrl"/> and
    ///     <see cref="ApiRuntimeSection.JwtAuthority"/> default to
    ///     <c>https://{primary host}[:{ApiHttps}]</c>. The scheme is always <c>https</c> because
    ///     the API container terminates HTTPS unconditionally in self-host (Kestrel's default
    ///     endpoint is wired to the bootstrapper-issued leaf PFX in
    ///     <c>InterfoldAppHost.ConfigureApiSelfHostEnv</c>, independent of
    ///     <see cref="DeploymentSection.WebHttps"/>, which only governs the <em>web</em>
    ///     container). The port suffix is omitted when <see cref="PortsSection.ApiHttps"/> is
    ///     443 so operators fronting the API with a 443-bound proxy get clean URLs.
    ///   </item>
    ///   <item>
    ///     <see cref="ApiRuntimeSection.CorsAllowedOrigins"/> defaults to one entry per non-CIDR
    ///     host of the form <c>{webScheme}://{host}[:{webPort}]</c>. CORS represents the
    ///     <em>client</em> origins (the wasm SPA + native deep-link callers); the SPA lands on
    ///     the web container, so the scheme follows <see cref="DeploymentSection.WebHttps"/> and
    ///     the port follows the matching <see cref="PortsSection.WebHttps"/> /
    ///     <see cref="PortsSection.WebHttp"/> mapping. The port suffix is omitted when the
    ///     operator picked the scheme's default port (80 for http, 443 for https).
    ///   </item>
    /// </list>
    /// IPv6 literals are bracket-wrapped per RFC 3986 §3.2.2 by <see cref="HostParser.ToUrlHost"/>.
    /// CIDR entries are skipped because they have no canonical URL form. Stored non-empty values
    /// always win — this method only fills blanks. Idempotent: calling it twice has no effect
    /// after the first.
    /// <para>
    /// Called by <see cref="Validate"/> before its invariant checks so non-interactive
    /// JSON-loaded configs go through the same derivation path as freshly-prompted ones (the
    /// interactive form's Show callbacks call this so the menu rows display the derived
    /// default next to their labels even before the operator presses Enter).
    /// </para>
    /// <para>
    /// Returns nothing — mutates the passed-in config in place. Internal so the unit tests can
    /// drive derivation directly and so <see cref="PromptForConfig"/>'s Show callbacks can call
    /// it on every redraw.
    /// </para>
    /// </summary>
    internal static void ResolveDerivedDefaults(BootstrapConfig config)
    {
        if (config.Deployment.Hosts.Count == 0)
        {
            return;
        }

        // Walk the raw strings, skipping anything that doesn't parse cleanly. ResolveDerivedDefaults
        // runs on EVERY menu redraw (via the Show callbacks), often mid-edit when the operator is
        // still typing, so a transient parse failure has to be silent here - Validate is the place
        // that hard-fails on invalid hosts. The same forgiving stance applies to CIDR entries:
        // they have no URL form, so they just don't contribute to derivation.
        var parsed = new List<HostEntry>(config.Deployment.Hosts.Count);
        foreach (var raw in config.Deployment.Hosts)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                parsed.Add(HostParser.Parse(raw));
            }
            catch (FormatException)
            {
                // Skip malformed entries silently; Validate surfaces the error to the operator.
            }
        }

        var primary = HostParser.PickPrimary(parsed);
        if (primary is null)
        {
            // No leaf-eligible entry yet (all CIDR, or list is empty after filtering) - nothing
            // to derive against. Validate will reject this config later with a precise error.
            return;
        }

        // API-facing URL: scheme is always https (the API container's Kestrel default endpoint
        // is bound to the bootstrapper-issued leaf PFX unconditionally - see InterfoldAppHost.
        // ConfigureApiSelfHostEnv, which sets ASPNETCORE_HTTPS_PORTS + Kestrel cert path on
        // every self-host run independent of DeploymentSection.WebHttps). The port suffix is
        // omitted when the operator chose 443 so the derived URL stays clean for stacks
        // fronted by a 443-bound proxy that re-publishes the API on the default https port.
        var apiPortSuffix = config.Ports.ApiHttps == DefaultHttpsPort
            ? string.Empty
            : $":{config.Ports.ApiHttps}";
        var apiDerivedBaseUrl = $"https://{HostParser.ToUrlHost(primary)}{apiPortSuffix}";

        if (string.IsNullOrWhiteSpace(config.ApiRuntime.CallbackBaseUrl))
        {
            config.ApiRuntime.CallbackBaseUrl = apiDerivedBaseUrl;
        }

        if (string.IsNullOrWhiteSpace(config.ApiRuntime.JwtAuthority))
        {
            config.ApiRuntime.JwtAuthority = apiDerivedBaseUrl;
        }

        if (config.ApiRuntime.CorsAllowedOrigins.Count == 0)
        {
            // CORS allow-list represents the *client* origins (the wasm SPA + native deep-link
            // callers). The SPA lands on the web container, so the scheme follows
            // DeploymentSection.WebHttps and the port follows the web tier's operator-chosen
            // mapping. Omit the port suffix when the operator picked the scheme's default port
            // (80 for http, 443 for https) — those origins are written canonically without it.
            var webHttps = config.Deployment.WebHttps;
            var webScheme = webHttps ? "https" : "http";
            var webPort = webHttps ? config.Ports.WebHttps : config.Ports.WebHttp;
            var webDefaultPort = webHttps ? DefaultHttpsPort : DefaultHttpPort;
            var webPortSuffix = webPort == webDefaultPort ? string.Empty : $":{webPort}";

            config.ApiRuntime.CorsAllowedOrigins = parsed
                .Where(h => h.IsLeafEligible)
                .Select(h => $"{webScheme}://{HostParser.ToUrlHost(h)}{webPortSuffix}")
                .ToList();
        }
    }

    /// <summary>
    /// Validates a loaded or freshly-prompted config. Internal so the unit-test project can
    /// drive validation failures directly without round-tripping through file IO + RunAsync.
    /// Throws <see cref="InvalidOperationException"/> with an operator-readable message on the
    /// first invariant that's broken.
    /// </summary>
    internal static void Validate(BootstrapConfig config)
    {
        if (config.Deployment.Hosts.Count == 0)
        {
            throw new InvalidOperationException(
                "config.deployment.hosts must contain at least one host (DNS name, IP literal, or CIDR).");
        }

        var parsedHosts = new List<HostEntry>(config.Deployment.Hosts.Count);
        foreach (var raw in config.Deployment.Hosts)
        {
            try
            {
                parsedHosts.Add(HostParser.Parse(raw));
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    $"config.deployment.hosts: {ex.Message}", ex);
            }
        }
        if (!parsedHosts.Any(h => h.IsLeafEligible))
        {
            throw new InvalidOperationException(
                "config.deployment.hosts must contain at least one non-CIDR entry to serve as the " +
                "primary host (leaf cert CN, nginx server_name, and derived URL defaults). " +
                "CIDR blocks restrict the root CA's Name Constraints but cannot stand alone.");
        }

        if (config.Deployment.CertYears is < 1 or > 30)
        {
            throw new InvalidOperationException(
                $"config.deployment.certYears={config.Deployment.CertYears} is outside the allowed 1..30 range.");
        }

        ValidatePort(config.Ports.ApiHttp, nameof(config.Ports.ApiHttp));
        ValidatePort(config.Ports.ApiHttps, nameof(config.Ports.ApiHttps));
        ValidatePort(config.Ports.WebHttp, nameof(config.Ports.WebHttp));
        ValidatePort(config.Ports.WebHttps, nameof(config.Ports.WebHttps));
        ValidatePort(config.Ports.Postgres, nameof(config.Ports.Postgres));
        ValidatePort(config.Ports.Scylla, nameof(config.Ports.Scylla));

        // Every host port in the bound set must be unique — docker compose can only bind a given
        // port on the host once per project, so any collision (api-http with web-http, postgres
        // with scylla, etc.) would surface as a "port already allocated" error deep inside the
        // launch phase. Catch it here with a clear operator message instead.
        var portFields = new (string Name, int Port)[]
        {
            (nameof(config.Ports.ApiHttp), config.Ports.ApiHttp),
            (nameof(config.Ports.ApiHttps), config.Ports.ApiHttps),
            (nameof(config.Ports.WebHttp), config.Ports.WebHttp),
            (nameof(config.Ports.WebHttps), config.Ports.WebHttps),
            (nameof(config.Ports.Postgres), config.Ports.Postgres),
            (nameof(config.Ports.Scylla), config.Ports.Scylla),
        };
        var seen = new Dictionary<int, string>(portFields.Length);
        foreach (var (name, port) in portFields)
        {
            if (seen.TryGetValue(port, out var other))
            {
                throw new InvalidOperationException(
                    $"config.ports.{char.ToLowerInvariant(name[0])}{name[1..]} ({port}) collides with " +
                    $"config.ports.{char.ToLowerInvariant(other[0])}{other[1..]}; every bound host port must be unique.");
            }
            seen[port] = name;
        }

        if (config.DatabaseMode is not ("single" or "multi" or "cassandra"))
        {
            throw new InvalidOperationException(
                $"config.databaseMode='{config.DatabaseMode}' is invalid. Expected: single | multi | cassandra. " +
                "(Translates to AppHost parameters include-scylla / include-cassandra / scylla-topology.)");
        }

        // The value flows into both the API connection string (Database=<name>) and the seeder's
        // CREATE DATABASE "<name>" call. Restricting to a Postgres-safe identifier keeps both
        // call sites quoting-safe and inside Postgres' 63-byte NAMEDATALEN budget. We deliberately
        // forbid leading digits and punctuation so the name doesn't need double-quoted-everywhere
        // handling and so it can also serve as a default role / schema prefix downstream.
        if (string.IsNullOrWhiteSpace(config.PostgresDatabase))
        {
            throw new InvalidOperationException(
                "config.postgresDatabase must be a non-empty Postgres identifier (default: 'interfold').");
        }
        if (!PostgresIdentifierPattern.IsMatch(config.PostgresDatabase))
        {
            throw new InvalidOperationException(
                $"config.postgresDatabase='{config.PostgresDatabase}' is not a safe Postgres identifier. " +
                "Allowed: 1..63 chars matching [A-Za-z_][A-Za-z0-9_]*.");
        }

        // ClusterName flows into Cassandra's CASSANDRA_CLUSTER_NAME (which the entrypoint
        // pastes into cassandra.yaml via an in-place rewrite) and Scylla's `--cluster-name`
        // CLI flag. Single quotes would break the YAML rewrite; newlines / control chars
        // would corrupt the YAML or the argv. The character set below is the intersection
        // of what both backends accept without quoting gymnastics: letters, digits, spaces,
        // dashes, dots, underscores. 1..64 chars matches Cassandra's documented limit.
        if (string.IsNullOrWhiteSpace(config.ClusterName))
        {
            throw new InvalidOperationException(
                "config.clusterName must be a non-empty cluster identifier (default: 'InterfoldCluster').");
        }
        if (!ClusterNamePattern.IsMatch(config.ClusterName))
        {
            throw new InvalidOperationException(
                $"config.clusterName='{config.ClusterName}' contains characters that would break " +
                "Cassandra's cassandra.yaml rewrite or Scylla's CLI argument parsing. " +
                "Allowed: 1..64 chars matching [A-Za-z0-9 ._-].");
        }

        // Scylla keyspace controls both the API's default keyspace and its region identity
        // for new-account routing. Constrained to the seven regional values the rest of the
        // codebase recognises (see ValidScyllaKeyspaces above) — anything outside that list
        // would produce a runtime "wrong region" failure rather than an upfront error.
        if (!ValidScyllaKeyspaces.Contains(config.ScyllaKeyspace, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"config.scyllaKeyspace='{config.ScyllaKeyspace}' is invalid. " +
                $"Expected one of: {string.Join(", ", ValidScyllaKeyspaces)}.");
        }

        // Materialise the derivable ApiRuntime defaults so the per-field validators below
        // see the post-derivation values (a non-interactive caller passing a JSON file with
        // empty apiRuntime.callbackBaseUrl etc. should get the same end result as the
        // interactive form).
        ResolveDerivedDefaults(config);

        ValidateAbsoluteHttpUri(config.ApiRuntime.CallbackBaseUrl, "config.apiRuntime.callbackBaseUrl");
        ValidateAbsoluteHttpUri(config.ApiRuntime.JwtAuthority, "config.apiRuntime.jwtAuthority");

        if (string.IsNullOrWhiteSpace(config.ApiRuntime.JwtAudience))
        {
            throw new InvalidOperationException(
                "config.apiRuntime.jwtAudience must be a non-empty token-audience identifier (default: 'octocon').");
        }

        // CORS allow-list: every entry must be a parseable origin (scheme + host[:port], no path).
        // ASP.NET Core's WithOrigins() does an exact string match against the request's Origin
        // header, so anything that doesn't round-trip through Uri.TryCreate with the HTTP/HTTPS
        // scheme would never match and is therefore a bootstrapper-time error.
        if (config.ApiRuntime.CorsAllowedOrigins.Count == 0)
        {
            throw new InvalidOperationException(
                "config.apiRuntime.corsAllowedOrigins must contain at least one origin after derivation. " +
                "Add at least one non-CIDR entry to deployment.hosts so derivation can produce a default, " +
                "or populate corsAllowedOrigins explicitly.");
        }
        foreach (var origin in config.ApiRuntime.CorsAllowedOrigins)
        {
            ValidateAbsoluteHttpUri(origin, "config.apiRuntime.corsAllowedOrigins entry");
        }

        // Cluster: NodeGroup is constrained to the three values the API's ApplyCluster pipeline
        // recognises. Lower-case comparison so an operator-typed "Primary" still passes.
        if (string.IsNullOrWhiteSpace(config.Cluster.NodeGroup) ||
            !ValidNodeGroups.Contains(config.Cluster.NodeGroup.ToLowerInvariant(), StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"config.cluster.nodeGroup='{config.Cluster.NodeGroup}' is invalid. " +
                $"Expected one of: {string.Join(", ", ValidNodeGroups)}.");
        }

        // Storage: both fields are optional, but when supplied must be well-formed. AvatarPublicBase
        // is a URL the API stitches into responses; AvatarStorageRoot is a container-side absolute
        // path (we don't try to verify the path exists — it lives inside the API container, not on
        // the bootstrapper host).
        ValidateOptionalAbsoluteHttpUri(config.Storage.AvatarPublicBase, "config.storage.avatarPublicBase");
        if (!string.IsNullOrEmpty(config.Storage.AvatarStorageRoot)
            && !Path.IsPathRooted(config.Storage.AvatarStorageRoot))
        {
            throw new InvalidOperationException(
                $"config.storage.avatarStorageRoot='{config.Storage.AvatarStorageRoot}' must be an " +
                "absolute path inside the API container (e.g. '/var/lib/interfold/avatars').");
        }

        // Observability: OTLP endpoint is optional but, when set, must be a valid http(s) URI —
        // the OpenTelemetry SDK accepts gRPC over HTTP/2 via the http:// scheme.
        ValidateOptionalAbsoluteHttpUri(config.Observability.OtlpEndpoint, "config.observability.otlpEndpoint");

        // Socket: nullable threshold; when supplied, must be a positive byte count up to 16 MiB.
        // Anything larger would defeat the purpose of batching (the message would always exceed
        // the cap before a flush check).
        if (config.Socket.BatchBytesThreshold is { } socketThreshold)
        {
            ValidateIntRange(socketThreshold, 1, 16 * 1024 * 1024, "config.socket.batchBytesThreshold");
        }

        // Persistence tuning: each int has its own sane range. The max-vs-initial cross-check
        // catches the easy swap mistake where an operator inverts the two values.
        ValidateIntRange(config.Persistence.DbRetryAttempts, 1, 100, "config.persistence.dbRetryAttempts");
        ValidateIntRange(config.Persistence.DbRetryInitialDelayMs, 1, 60_000,
            "config.persistence.dbRetryInitialDelayMs");
        ValidateIntRange(config.Persistence.DbRetryMaxDelayMs, 1, 600_000,
            "config.persistence.dbRetryMaxDelayMs");
        if (config.Persistence.DbRetryMaxDelayMs < config.Persistence.DbRetryInitialDelayMs)
        {
            throw new InvalidOperationException(
                $"config.persistence.dbRetryMaxDelayMs ({config.Persistence.DbRetryMaxDelayMs}) " +
                $"must be >= dbRetryInitialDelayMs ({config.Persistence.DbRetryInitialDelayMs}).");
        }
        ValidateIntRange(config.Persistence.HydrationMaxConcurrency, 1, 1024,
            "config.persistence.hydrationMaxConcurrency");

        // Backup + autostart: every field defaults to a no-op stance, so the validator only
        // matters for operators that opted in. The schedule check is intentionally permissive
        // — we don't reimplement systemd's calendar parser, just reject obvious typos
        // (empty/control chars). SystemdInstallPhase shells out to `systemd-analyze calendar`
        // at install time for the real validation, but catching the egregious cases here gives
        // operators a faster feedback loop than running through the publish path.
        ValidateIntRange(config.Backup.RetainCount, 1, 1000, "config.backup.retainCount");
        if (string.IsNullOrWhiteSpace(config.Backup.Schedule))
        {
            throw new InvalidOperationException(
                "config.backup.schedule must be a non-empty systemd OnCalendar expression " +
                "(e.g. 'daily', 'weekly', or 'Mon..Fri 03:30').");
        }
        if (!BackupSchedulePattern.IsMatch(config.Backup.Schedule))
        {
            throw new InvalidOperationException(
                $"config.backup.schedule='{config.Backup.Schedule}' contains characters that are not " +
                "valid in a systemd OnCalendar expression. Allowed: letters, digits, spaces, and " +
                "the punctuation '.-:,*/'.");
        }
        if (!string.IsNullOrEmpty(config.Backup.Directory)
            && !Path.IsPathRooted(config.Backup.Directory))
        {
            throw new InvalidOperationException(
                $"config.backup.directory='{config.Backup.Directory}' must be an absolute path " +
                "(systemd-driven backup invocations have an unpredictable CWD; relative paths " +
                "would not resolve consistently). Leave blank to default to '{outputDir}/backups'.");
        }

        // Update section: the health-check timeout must sit in a range that covers a
        // realistic cold-start (Postgres+Scylla+API on modest hardware routinely reach
        // 60-120s) without letting a broken image loop indefinitely — 3600s = 1h upper
        // bound matches the "give up eventually" contract UpdateImagesPhase relies on.
        ValidateIntRange(
            config.Update.HealthCheckTimeoutSeconds, 1, 3600,
            "config.update.healthCheckTimeoutSeconds");

        // Every entry in the whitelist must be a known compose service name. Empty
        // (the default) means "every service" and is valid; only non-empty entries
        // are checked so an operator that ignores the feature entirely still produces
        // a valid config.
        foreach (var svc in config.Update.Services)
        {
            if (string.IsNullOrWhiteSpace(svc))
            {
                throw new InvalidOperationException(
                    "config.update.services contains a blank entry. Remove it or " +
                    $"replace with one of: {string.Join(", ", ValidUpdateServices)}.");
            }
            if (!ValidUpdateServices.Contains(svc, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"config.update.services entry '{svc}' is not a known compose service. " +
                    $"Expected one of: {string.Join(", ", ValidUpdateServices)}.");
            }
        }
    }

    /// <summary>
    /// Shared validator for the three URL-shaped <see cref="ApiRuntimeSection"/> fields.
    /// Accepts absolute HTTP/HTTPS URIs; rejects anything else with the field name in the
    /// error so the operator can tell which row needs fixing. Internal so the unit tests can
    /// drive validation failures directly without re-deriving values.
    /// </summary>
    private static void ValidateAbsoluteHttpUri(string value, string fieldLabel)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{fieldLabel} must be a non-empty http(s) URL.");
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"{fieldLabel}='{value}' is not a valid absolute http(s) URL.");
        }
    }

    /// <summary>
    /// Sibling of <see cref="ValidateAbsoluteHttpUri"/> for fields whose blank-state is also
    /// valid (e.g. <see cref="StorageSection.AvatarPublicBase"/>,
    /// <see cref="ObservabilitySection.OtlpEndpoint"/>). Empty/whitespace passes through; a
    /// non-empty value still has to parse as an absolute http(s) URI.
    /// </summary>
    private static void ValidateOptionalAbsoluteHttpUri(string value, string fieldLabel)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        ValidateAbsoluteHttpUri(value, fieldLabel);
    }

    /// <summary>
    /// Shared range check for the tuning ints under
    /// <see cref="PersistenceTuningSection"/> / <see cref="SocketSection"/>. Reports the field
    /// label and observed value so the operator-facing error matches the JSON key path that
    /// caused it.
    /// </summary>
    private static void ValidateIntRange(int value, int min, int max, string fieldLabel)
    {
        if (value < min || value > max)
        {
            throw new InvalidOperationException(
                $"{fieldLabel}={value} is outside the allowed [{min}..{max}] range.");
        }
    }

    private static readonly Regex PostgresIdentifierPattern =
        new("^[A-Za-z_][A-Za-z0-9_]{0,62}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ClusterNamePattern =
        new("^[A-Za-z0-9 ._-]{1,64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Character-class allow-list for the <see cref="BackupSection.Schedule"/> field. Covers
    /// every glyph systemd accepts in a <c>OnCalendar=</c> directive (the shortcuts
    /// <c>hourly|daily|weekly|monthly</c>, day-of-week tokens like <c>Mon..Fri</c>, and the
    /// full <c>YYYY-MM-DD HH:MM:SS</c> form including <c>*</c>/<c>/</c> wildcards). We
    /// deliberately don't try to enforce the grammar — <c>systemd-analyze calendar</c> does
    /// that authoritatively at install time. This pattern only rejects clearly-bogus inputs
    /// (control chars, shell metacharacters) so the operator gets feedback at config-load
    /// time instead of three commands later.
    /// </summary>
    private static readonly Regex BackupSchedulePattern =
        new(@"^[A-Za-z0-9 .,:\-*/]{1,256}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static void ValidatePort(int port, string field)
    {
        if (port is < 1 or > 65535)
        {
            throw new InvalidOperationException($"{field}={port} is outside the valid 1..65535 range.");
        }
    }

    private static async Task PersistAsync(BootstrapConfig config, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(config, BootstrapJsonContext.Default.BootstrapConfig);
        await File.WriteAllTextAsync(path, json, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Pre-prompt mDNS status banner. Runs before <see cref="PromptForConfig"/> on the
    /// interactive fresh-config branch of <see cref="RunAsync"/>. Detects the device hostname,
    /// probes whether <c>{hostname}.local</c> resolves via the local mDNS stack, prints a
    /// status banner up-front, and (when mDNS is unavailable AND we have a TTY) optionally
    /// offers to install <c>avahi-daemon</c> + <c>libnss-mdns</c> right there. Returns the
    /// hostname to seed the "Public host(s)" prompt with, or <c>null</c> if we should skip the
    /// pre-fill (no hostname detected, non-Linux, probe failed and operator declined install,
    /// install completed but the re-probe still failed).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is Tier 1 of the two-tier mDNS design. Tier 2 (<see cref="ApplyMdnsGateAsync"/>)
    /// runs after <see cref="Validate"/> and catches <c>.local</c> entries that survived the
    /// operator's typing regardless of what the banner said, plus JSON-load paths that
    /// bypass the banner entirely. Both tiers share the same underlying probe / install
    /// primitives from <see cref="MdnsAvailability"/> and <see cref="PrerequisitesPhase.RunInstallAsync"/>.
    /// </para>
    /// <para>
    /// Non-interactive callers are short-circuited immediately: the banner is a TTY-only
    /// concept (there's no operator to talk to). The gate handles unresolvable <c>.local</c>
    /// entries in that flow.
    /// </para>
    /// </remarks>
    /// <param name="options">Bootstrap options; the <see cref="BootstrapOptions.NonInteractive"/> flag short-circuits.</param>
    /// <param name="logger">Phase logger for the info/warn banner messages.</param>
    /// <param name="ct">Cancellation token propagated into the probe and (optional) install.</param>
    /// <param name="hostnameFactory">
    /// Test seam for the hostname detection step. Defaults to
    /// <see cref="HostnameDetector.TryDetectMdnsHostname"/> in production; unit tests inject
    /// a fake returning either a canned <c>"workstation.local"</c> or <c>null</c> to exercise
    /// the no-hostname branch deterministically.
    /// </param>
    /// <param name="probe">
    /// Test seam for the resolvability probe. Defaults to
    /// <see cref="MdnsAvailability.IsHostnameResolvableAsync"/> in production; unit tests
    /// inject a fake returning <c>true</c> / <c>false</c> / <c>null</c> to exercise the
    /// resolves / broken / non-Linux branches without shelling out to getent.
    /// </param>
    internal static async Task<string?> ApplyPreFillMdnsCheckAsync(
        BootstrapOptions options,
        PhaseLogger logger,
        CancellationToken ct,
        Func<string?>? hostnameFactory = null,
        Func<string, CancellationToken, Task<bool?>>? probe = null)
    {
        // Non-interactive callers get no banner — no TTY to talk to. The post-fill gate
        // still handles any .local entries the operator supplied in their config file.
        if (options.NonInteractive)
        {
            return null;
        }

        var hostname = (hostnameFactory ?? HostnameDetector.TryDetectMdnsHostname)();
        if (hostname is null)
        {
            logger.Info("    mDNS pre-check: no suitable short hostname detected; skipping .local pre-fill");
            return null;
        }

        var probeFn = probe ?? MdnsAvailability.IsHostnameResolvableAsync;
        var initial = await probeFn(hostname, ct).ConfigureAwait(false);
        if (initial is null)
        {
            logger.Info($"    mDNS pre-check: platform doesn't support probing; skipping {hostname} pre-fill");
            return null;
        }
        if (initial == true)
        {
            logger.Info($"    mDNS pre-check: {hostname} resolves; will pre-fill it in the hosts prompt");
            return hostname;
        }

        // mDNS is unavailable. Warn the operator up-front so they see the status BEFORE any
        // hosts prompt appears; then, if we have a TTY, offer to install avahi. If they
        // decline (or the install fails), we omit the .local name from the pre-fill.
        var distro = DistroInfo.Read();
        logger.Warn($"mDNS is unavailable on this device — {hostname} will NOT resolve on the LAN.");
        logger.Warn($"    to enable it: {MdnsAvailability.ManualInstallHint(distro.Family)}");

        // Console.IsInputRedirected == true means we can't drive a confirmation prompt (the
        // unit-test path lands here). Skip the offer and let the operator handle it later
        // via the install hint above. Same guard the outer RunAsync uses.
        if (Console.IsInputRedirected)
        {
            logger.Warn($"    no TTY for install prompt; {hostname} will be omitted from the pre-fill");
            return null;
        }

        var install = AnsiConsole.Prompt(new ConfirmationPrompt(
            $"Install avahi-daemon + nss-mdns now so {hostname} can be included in the hosts list?")
        { DefaultValue = true });
        if (!install)
        {
            logger.Warn($"    operator declined install — {hostname} will be omitted from the pre-fill");
            return null;
        }

        var packages = MdnsAvailability.InstallPackages(distro.Family);
        if (packages.Count == 0)
        {
            logger.Warn($"    unsupported distro family {distro.Family}; skipping install and .local pre-fill");
            return null;
        }
        logger.Info($"    installing avahi ({string.Join(" ", packages)}) ...");
        try
        {
            await PrerequisitesPhase.RunInstallAsync(distro, packages, logger, ct).ConfigureAwait(false);
            // Try to bring the daemon up. Failure here isn't fatal — some minimal environments
            // won't have systemctl at all, in which case the operator can `service avahi-daemon
            // start` manually. We swallow errors and fall through to the re-probe, which will
            // decide whether things now work.
            try
            {
                await ProcessRunner.RunAsync(
                    "systemctl", ["enable", "--now", "avahi-daemon"], ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Warn($"    could not `systemctl enable --now avahi-daemon` ({ex.GetType().Name}: {ex.Message}); " +
                            "start it manually if the re-probe below fails.");
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"    avahi install failed ({ex.GetType().Name}: {ex.Message}); {hostname} will be omitted from the pre-fill");
            return null;
        }

        var recheck = await probeFn(hostname, ct).ConfigureAwait(false);
        if (recheck == true)
        {
            logger.Info($"    mDNS now resolvable; pre-filling {hostname}");
            return hostname;
        }
        logger.Warn($"    avahi installed but {hostname} still doesn't resolve; omitting from pre-fill");
        return null;
    }

    /// <summary>
    /// Post-fill mDNS safety gate. Runs after <see cref="Validate"/> on the
    /// <see cref="BootstrapCommand.Bootstrap"/> command only. Probes every <c>.local</c> entry
    /// in the finalised <see cref="DeploymentSection.Hosts"/> list; if any fail to resolve,
    /// interactively offers to install avahi (identical to the pre-prompt banner's install
    /// path) and, when that's declined / fails / non-interactive, strips the unresolvable
    /// entries with a warning + copy-pasteable install hint. Bootstrap always continues:
    /// re-running <c>bootstrap</c> after installing mDNS is <em>optional recovery</em>, not
    /// a required next step.
    /// </summary>
    /// <returns>
    /// True when the hosts list was mutated (caller must re-validate and, on the interactive
    /// branch, re-persist the config to keep the JSON in sync). False when the gate was a
    /// no-op — no <c>.local</c> entries, non-Linux short-circuit, everything resolved
    /// successfully, or the install completed and the re-probe now passes.
    /// </returns>
    /// <remarks>
    /// Partial mDNS resolution is treated as broken — if ANY <c>.local</c> in the list fails
    /// to resolve, we ask about install / strip the failing entries. Reasoning: a leaf cert
    /// SAN chain is trust-all-or-nothing; issuing a cert where half the SANs point at names
    /// that don't resolve gives the operator a broken deployment that fails only for some
    /// clients, which is worse than a smaller-but-consistent SAN set.
    /// </remarks>
    /// <param name="config">Config being bootstrapped; mutated in place when entries are stripped.</param>
    /// <param name="options">Bootstrap options; drives the interactive-vs-strip fork.</param>
    /// <param name="logger">Phase logger for the warn + install-hint messages.</param>
    /// <param name="ct">Cancellation token propagated into the probe and (optional) install.</param>
    /// <param name="probe">
    /// Test seam for the resolvability probe. Defaults to
    /// <see cref="MdnsAvailability.IsHostnameResolvableAsync"/> in production; unit tests
    /// inject a fake to drive skip / accept / strip / re-validate branches without shelling
    /// out to getent.
    /// </param>
    internal static async Task<bool> ApplyMdnsGateAsync(
        BootstrapConfig config,
        BootstrapOptions options,
        PhaseLogger logger,
        CancellationToken ct,
        Func<string, CancellationToken, Task<bool?>>? probe = null)
    {
        var localHosts = config.Deployment.Hosts
            .Where(h => h.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (localHosts.Count == 0)
        {
            return false;
        }

        var probeFn = probe ?? MdnsAvailability.IsHostnameResolvableAsync;

        // Probe every .local host up-front. A null result from any of them means "can't
        // probe on this platform" — same short-circuit the pre-fill banner uses. We could
        // in principle only check the first .local and treat that as representative, but
        // running getent per-entry means a mid-list rename to a resolvable name doesn't
        // mask an earlier broken one.
        var broken = new List<string>();
        foreach (var host in localHosts)
        {
            var result = await probeFn(host, ct).ConfigureAwait(false);
            if (result is null)
            {
                return false;
            }
            if (result == false)
            {
                broken.Add(host);
            }
        }
        if (broken.Count == 0)
        {
            logger.Info($"    mDNS ok: {string.Join(", ", localHosts)} all resolvable");
            return false;
        }

        // Interactive install offer — only when we have a TTY. Non-interactive callers fall
        // straight through to the strip + warn path below. The banner may already have asked
        // this question, but the operator could still have typed a .local name manually in
        // the "Public host(s)" prompt after declining, and JSON-load runs never see the
        // banner at all; either way, offering here catches the "changed my mind, install
        // now" case.
        if (!options.NonInteractive && !Console.IsInputRedirected)
        {
            var distro = DistroInfo.Read();
            var install = AnsiConsole.Prompt(new ConfirmationPrompt(
                $"mDNS is unavailable but your hosts list contains {string.Join(", ", broken)}. " +
                "Install avahi-daemon + nss-mdns now?")
            { DefaultValue = true });
            if (install)
            {
                var packages = MdnsAvailability.InstallPackages(distro.Family);
                if (packages.Count > 0)
                {
                    logger.Info($"    installing avahi ({string.Join(" ", packages)}) ...");
                    try
                    {
                        await PrerequisitesPhase.RunInstallAsync(distro, packages, logger, ct).ConfigureAwait(false);
                        try
                        {
                            await ProcessRunner.RunAsync(
                                "systemctl", ["enable", "--now", "avahi-daemon"], ct: ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            logger.Warn($"    could not `systemctl enable --now avahi-daemon` ({ex.GetType().Name}: {ex.Message}); " +
                                        "start it manually and re-run bootstrap if the re-probe below still fails.");
                        }
                        // Re-probe every previously-broken host. If any still fail (e.g. NAT'd
                        // LAN, the daemon isn't advertising us yet), we fall through to strip
                        // so the current run still produces a usable cert.
                        var stillBroken = false;
                        foreach (var host in broken)
                        {
                            if (await probeFn(host, ct).ConfigureAwait(false) != true)
                            {
                                stillBroken = true;
                                break;
                            }
                        }
                        if (!stillBroken)
                        {
                            logger.Info("    mDNS now resolvable for all .local hosts");
                            return false;
                        }
                        logger.Warn($"avahi installed but {string.Join(", ", broken)} still doesn't resolve; removing from hosts.");
                    }
                    catch (Exception ex)
                    {
                        logger.Warn($"avahi install failed ({ex.GetType().Name}: {ex.Message}); removing unresolvable .local host(s).");
                    }
                }
            }
        }

        // Strip + warn + continue. Bootstrap does NOT halt — it finishes with the reduced
        // hosts list. The install hint tells the operator how to re-enable those names on a
        // future `bootstrap` run if they want them back; it's recovery advice, not a
        // requirement for the current run to succeed.
        var installHint = MdnsAvailability.ManualInstallHint(DistroInfo.Read().Family);
        logger.Warn($"removing unresolvable .local host(s) from config.deployment.hosts: {string.Join(", ", broken)}");
        logger.Warn($"    bootstrap will continue with the remaining hosts. To restore these entries on a future run, set up mDNS first: {installHint}");
        config.Deployment.Hosts = [.. config.Deployment.Hosts.Except(broken, StringComparer.OrdinalIgnoreCase)];
        return true;
    }
}
