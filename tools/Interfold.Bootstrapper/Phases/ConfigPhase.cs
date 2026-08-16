using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Configuration.Validation;
using Interfold.Shared.Contracts.Enums;
using Spectre.Console;

namespace Interfold.Bootstrapper.Phases;

/// <summary>Phase 2 — loads <c>interfold.bootstrap.json</c> or builds it via a Spectre.Console
/// walkthrough (sectioned prompts → review table → edit loop), validates, and persists.</summary>
internal static class ConfigPhase
{
    public static async Task<BootstrapConfig> RunAsync(BootstrapOptions options, PhaseLogger logger, CancellationToken ct)
    {
        const string Phase = "config";
        logger.PhaseStart(Phase);

        var configPath = BootstrapArtifactPaths.ResolveConfigPath(options);
        BootstrapConfig config;
        if (File.Exists(configPath))
        {
            logger.Info($"    loading config from {configPath}");
            var json = await File.ReadAllTextAsync(configPath, ct).ConfigureAwait(false);
            config = JsonSerializer.Deserialize(json, BootstrapJsonContext.Default.BootstrapConfig)
                     ?? throw new InvalidOperationException($"Failed to parse {configPath} (returned null).");

            if (options.Reconfigure)
            {
                if (options.NonInteractive || Console.IsInputRedirected)
                {
                    logger.PhaseFail(Phase, PhaseFailureReasons.ReconfigureRequiresInteractive);
                    throw new InvalidOperationException(
                        "--reconfigure requires an interactive TTY. " +
                        "Omit --non-interactive and run from a terminal (stdin must not be redirected).");
                }

                logger.Info("    --reconfigure: opening interactive editor seeded from existing config");
                var mdnsHostname = await ApplyPreFillMdnsCheckAsync(options, logger, ct).ConfigureAwait(false);
                config = PromptForConfig(
                    AnsiConsole.Console,
                    maskSecrets: true,
                    hostnameProbe: () => mdnsHostname,
                    existing: config);
                await PersistAsync(config, configPath, ct).ConfigureAwait(false);
            }
        }
        else if (options.NonInteractive)
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.MissingConfigNonInteractive);
            throw new InvalidOperationException(
                $"Config file not found at {configPath} and --non-interactive was set. " +
                "Provide --config <path> or rerun interactively.");
        }
        else if (!Console.IsInputRedirected)
        {
            logger.Info($"    no config at {configPath}; entering interactive setup");
            // mdnsHostname is null when .local won't resolve, so we don't pre-fill a broken name.
            var mdnsHostname = await ApplyPreFillMdnsCheckAsync(options, logger, ct).ConfigureAwait(false);

            // Tests pass maskSecrets: false so Spectre's ReadKey secret path doesn't
            // fight the TestConsole input queue.
            config = PromptForConfig(
                AnsiConsole.Console,
                maskSecrets: true,
                hostnameProbe: () => mdnsHostname);
            await PersistAsync(config, configPath, ct).ConfigureAwait(false);
        }
        else
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.MissingConfigNoTty);
            throw new InvalidOperationException(
                $"Config file not found at {configPath} and no TTY is available. " +
                "Run with --config <path> pointing at a populated interfold.bootstrap.json.");
        }

        Validate(config);

        // Post-fill mDNS gate is bootstrap-only — other subcommands load JSON verbatim.
        if (options.Command == BootstrapCommand.Bootstrap)
        {
            var mutated = await ApplyMdnsGateAsync(config, options, logger, ct).ConfigureAwait(false);
            if (mutated)
            {
                // Re-validate — a strip could have emptied the list, and issuing a cert with
                // zero SANs later would be a harder failure to diagnose.
                Validate(config);
                logger.Info($"    updated {configPath} to reflect the mDNS strip");
                await PersistAsync(config, configPath, ct).ConfigureAwait(false);
            }
        }

        if (!string.Equals(Path.GetFullPath(config.Deployment.OutputDir), options.OutputDir, StringComparison.Ordinal))
        {
            logger.Info($"    overriding config.outputDir with --output-dir={options.OutputDir}");
            config.Deployment.OutputDir = options.OutputDir;
        }

        logger.PhaseDone(Phase);
        return config;
    }

    /// <summary>Spectre.Console navigable form for <see cref="BootstrapConfig"/>. Each field
    /// is a menu row; arrows navigate, Enter edits, trailing <c>Confirm and save</c> returns.
    /// Test seams: <paramref name="localAddressProbe"/> and <paramref name="hostnameProbe"/>
    /// default to real detection but are stubbed out in unit tests for determinism.
    /// <paramref name="hostnameProbe"/>'s value MUST land first in the seed list so
    /// <see cref="ResolveDerivedDefaults"/> latches the mDNS name for the leaf cert.
    /// When <paramref name="existing"/> is supplied (e.g. <c>--reconfigure</c>), the form
    /// opens pre-filled; hostname/IP auto-seed only applies when Hosts is still empty.</summary>
    internal static BootstrapConfig PromptForConfig(
        IAnsiConsole console,
        bool maskSecrets = false,
        Func<IPAddress?>? localAddressProbe = null,
        Func<string?>? hostnameProbe = null,
        BootstrapConfig? existing = null)
    {
        var c = existing ?? new BootstrapConfig();

        // Hostname before IP so HostParser.PickPrimary latches the mDNS name.
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
                // Bare literal; HostParser.ToUrlHost brackets IPv6 later.
                seed.Add(detected.ToString());
            }
            if (seed.Count > 0)
            {
                c.Deployment.Hosts = seed;
            }
        }

        // Local helpers close over `console` and `maskSecrets` to keep each Edit lambda short.
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
            if (maskSecrets) p.Secret('*');
            return console.Prompt(p);
        }

        // Blank → null (use API default). Manual TextPrompt<string> because Spectre lacks TextPrompt<int?>.
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

        // Grouped fields — the flat `fields` list and `sections` array are DERIVED from
        // this so headers can't drift. Tuple is (Label, Show, Edit).
        (string Header, (string Label, Func<string> Show, Action Edit)[] Fields) Group(
            string header,
            params (string Label, Func<string> Show, Action Edit)[] fs) => (header, fs);

        var groups = new[]
        {
            Group("Deployment",
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
                // Publish wiring auto-promotes IncludeWeb when WebHttps is true.
                ("Include octocon-web container",   () => c.Deployment.IncludeWeb.ToString(),
                                                    () => c.Deployment.IncludeWeb = PromptBool("Include the octocon-web (Kotlin/Wasm UI) container", c.Deployment.IncludeWeb)),
                ("Terminate HTTPS at octocon-web",  () => c.Deployment.WebHttps.ToString(),
                                                    () => c.Deployment.WebHttps = PromptBool("Terminate HTTPS at octocon-web", c.Deployment.WebHttps))),

            Group("Ports",
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
                                                    () => c.Ports.Scylla = PromptInt("Scylla/Cassandra host port", c.Ports.Scylla, 1, 65535))),

            Group("Database",
                ("Database mode",                   () => c.DatabaseMode.ToWire(),
                                                    () => c.DatabaseMode = console.Prompt(
                                                        new TextPrompt<string>("Database mode:")
                                                            .DefaultValue(c.DatabaseMode.ToWire())
                                                            .AddChoices(ValidDatabaseModes)).TryParseWire<DatabaseMode>(out var mode) ? mode : c.DatabaseMode),
                ("Postgres application DB name",    () => c.PostgresDatabase,
                                                    () => c.PostgresDatabase = PromptStr("Postgres application DB name", c.PostgresDatabase)),
                ("Cluster name",                    () => c.ClusterName,
                                                    () => c.ClusterName = PromptStr("Cluster name (Scylla/Cassandra)", c.ClusterName)),
                // AddChoices enforces the seven valid keyspaces (Validate mirrors this non-interactively).
                ("Scylla keyspace (region)",        () => c.ScyllaKeyspace.ToWire(),
                                                    () => c.ScyllaKeyspace = EnumWireExtensions.ParseScyllaKeyspace(console.Prompt(
                                                        new TextPrompt<string>("Scylla keyspace (region):")
                                                            .DefaultValue(c.ScyllaKeyspace.ToWire())
                                                            .AddChoices(ValidScyllaKeyspaces))))),

            // Derivable rows snapshot into ResolveDerivedDefaults so the menu paints the
            // computed default before Enter.
            Group("API",
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
                                                    () => c.ApiImage = PromptStr("Pre-built Interfold API image reference", c.ApiImage))),

            Group("Cluster & telemetry",
                ("Cluster node group",              () => c.Cluster.NodeGroup.ToWire(),
                                                    () => c.Cluster.NodeGroup = EnumWireExtensions.ParseNodeGroup(console.Prompt(
                                                        new TextPrompt<string>("Cluster node group:")
                                                            .DefaultValue(c.Cluster.NodeGroup.ToWire())
                                                            .AddChoices(ValidNodeGroups)))),
                ("OTLP endpoint",                   () => ShowOrEmpty(c.Observability.OtlpEndpoint),
                                                    () => c.Observability.OtlpEndpoint = PromptStr(
                                                        "OTLP endpoint (blank to disable)",
                                                        c.Observability.OtlpEndpoint))),

            // Storage rows are optional. Blank AvatarStorageRoot → AppHost mounts
            // `interfold_avatars` at /app/data/avatars (non-blank puts the burden on
            // the operator for UID 1654 permissions). Blank AvatarPublicBase → API
            // serves /avatars/* directly.
            Group("Storage",
                ("Avatar storage root (container path)", () => ShowOrEmpty(c.Storage.AvatarStorageRoot),
                                                    () => c.Storage.AvatarStorageRoot = PromptStr(
                                                        "Avatar storage root (blank = /app/data/avatars, persisted by AppHost-managed volume; non-blank = you manage the mount and ownership)",
                                                        c.Storage.AvatarStorageRoot)),
                ("Avatar public base URL",          () => ShowOrEmpty(c.Storage.AvatarPublicBase),
                                                    () => c.Storage.AvatarPublicBase = PromptStr(
                                                        "Avatar public base URL (blank = API serves /avatars/* directly; set https URL to delegate to CDN)",
                                                        c.Storage.AvatarPublicBase))),

            Group("Performance tuning",
                // Blank → null (API compile-time default); Show renders "<default>" for null.
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
                                                        c.Persistence.HydrationMaxConcurrency, 1, 1024))),

            // ID (public → ShowOrEmpty) then secret (masked → <set>/<empty>) per provider.
            Group("OAuth credentials",
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
                                                    () => c.OAuth.AppleClientSecret = PromptOAuth("Apple OAuth client secret", c.OAuth.AppleClientSecret))),

            Group("Backup & autostart",
                ("Scheduled backups enabled",       () => c.Backup.Enabled.ToString(),
                                                    () => c.Backup.Enabled = PromptBool("Enable scheduled backups (systemd timer)", c.Backup.Enabled)),
                ("Backup schedule (OnCalendar)",    () => c.Backup.Schedule,
                                                    () => c.Backup.Schedule = PromptStr("Backup schedule (systemd OnCalendar, e.g. 'daily', 'weekly', 'Mon..Fri 03:30')", c.Backup.Schedule)),
                ("Backup retention (count per component)", () => c.Backup.RetainCount.ToString(),
                                                    () => c.Backup.RetainCount = PromptInt("Backup retention (number of archives to keep per component)", c.Backup.RetainCount, 1, 1000)),
                // Blank = {outputDir}/backups; non-blank must be absolute (systemd CWD unpredictable).
                ("Backup directory (absolute, blank=default)", () => ShowOrEmpty(c.Backup.Directory),
                                                    () => c.Backup.Directory = PromptStr("Backup directory (absolute path; leave blank to default to {outputDir}/backups)", c.Backup.Directory)),
                ("Autostart server on boot",        () => c.Backup.AutostartServer.ToString(),
                                                    () => c.Backup.AutostartServer = PromptBool("Autostart the server on host boot (installs interfold.service)", c.Backup.AutostartServer))),

            // Chains via systemd OnSuccess= drop-in. Off by default; manual works regardless.
            Group("Updates",
                ("Chain updates after backup",      () => c.Update.Enabled.ToString(),
                                                    () => c.Update.Enabled = PromptBool("Chain interfold-update.service after each successful backup", c.Update.Enabled)),
                ("Health-check timeout (seconds)",  () => c.Update.HealthCheckTimeoutSeconds.ToString(),
                                                    () => c.Update.HealthCheckTimeoutSeconds = PromptInt("Health-check timeout after pull+recreate (seconds)", c.Update.HealthCheckTimeoutSeconds, 1, 3600)),
                ("Auto-restore on failure",         () => c.Update.AutoRestoreOnFailure.ToString(),
                                                    () => c.Update.AutoRestoreOnFailure = PromptBool("Auto-restore the pre-update backup on health-check failure (destructive)", c.Update.AutoRestoreOnFailure)),
                ("Recreate containers on update",   () => c.Update.RecreateOnUpdate.ToString(),
                                                    () => c.Update.RecreateOnUpdate = PromptBool("Recreate containers on update (uses 'up -d'; disable only for staged pulls)", c.Update.RecreateOnUpdate)),
                ("Update service whitelist (blank=all)", () => c.Update.Services.Length == 0 ? "<all>" : string.Join(",", c.Update.Services),
                                                    () => c.Update.Services = PromptUpdateServices(console, c.Update.Services))),

            // One row → three-way wizard (auto-detect / per-file / clear). See PromptFirebase.
            Group("Firebase",
                ("Firebase push notifications",     () => ShowFirebaseState(c.Firebase),
                                                    () => PromptFirebase(console, c.Firebase))),
        };

        // Flatten groups; record each group's starting offset for AddChoiceGroup and header sentinels.
        var fields = new List<(string Label, Func<string> Show, Action Edit)>();
        var sectionsList = new List<(int FirstFieldIndex, string Header)>(groups.Length);
        foreach (var g in groups)
        {
            sectionsList.Add((fields.Count, g.Header));
            fields.AddRange(g.Fields);
        }
        var sections = sectionsList.ToArray();

        // Sentinels: -1 Confirm, 0..N-1 field indices, -(2+sectionIdx) inert header (must
        // be unique per section for AddChoiceGroup's key contract).
        const int ConfirmSentinel = -1;
        int SectionHeaderSentinel(int sectionIdx) => -(2 + sectionIdx);
        int SectionLength(int sectionIdx) =>
            (sectionIdx + 1 < sections.Length ? sections[sectionIdx + 1].FirstFieldIndex : fields.Count)
            - sections[sectionIdx].FirstFieldIndex;

        while (true)
        {
            var prompt = new SelectionPrompt<int>()
                .Title(
                    "[bold]Configure interfold.bootstrap.json[/]\n" +
                    "[grey]Use arrow keys to navigate, Enter to edit, choose [green]Confirm and save[/] when done.[/]")
                // ~60 rows (48 fields + 11 headers + 1 confirm) — oversize so large TTYs
                // don't scroll; smaller TTYs paginate via MoreChoicesText.
                .PageSize(60)
                .MoreChoicesText("[grey](move up/down to reveal more)[/]")
                // Escape values — a `[` in operator input would otherwise crash Spectre's parser.
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

    /// <summary>Comma-separated hosts prompt (DNS / IP / CIDR).</summary>
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
                        // Empty only OK when the fallback is non-empty; fresh bootstrap
                        // must force input so we never issue a cert with no SANs.
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

    /// <summary>CORS allow-list prompt; each entry must be an absolute http(s) URI (matches
    /// <see cref="Validate"/>).</summary>
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

    /// <summary>Blank = every service; non-empty is validated against
    /// <see cref="ValidUpdateServices"/> so typos don't surface later as "no such service".</summary>
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

    /// <summary>Menu-row summary: <c>off</c> or <c>N/4 configured (...)</c>.</summary>
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

    // Referenced by exact string in tests — drift breaks the interactive seam silently.
    private const string FirebaseChoiceAutoDetect = "Auto-detect from folder";
    private const string FirebaseChoicePerFile = "Configure per-file";
    private const string FirebaseChoiceClear = "Clear all";
    private const string FirebaseChoiceCancel = "Cancel";

    /// <summary>Auto-detect / per-file / clear / cancel wizard. Mutates
    /// <paramref name="section"/> in place so the menu redraws with the new summary.</summary>
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

    /// <summary>Folder branch: scans, writes resolved paths, prints a found/missing summary.
    /// Blank aborts silently.</summary>
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

    /// <summary>Blank leaves the platform unwired; non-blank must resolve to an existing
    /// file so a typo doesn't silently disable push.</summary>
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

    /// <summary>Two-column table showing which platforms auto-detect resolved.</summary>
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

    /// <summary>Renders derived default when the field is empty; operator value wins otherwise.
    /// One helper for CallbackBaseUrl + JwtAuthority via the selector.</summary>
    private static string DerivedShow(BootstrapConfig c, Func<ApiRuntimeSection, string> selector)
    {
        var snapshot = CloneForDerivation(c);
        ResolveDerivedDefaults(snapshot);
        return selector(snapshot.ApiRuntime);
    }

    /// <summary>List-shaped sibling of <see cref="DerivedShow"/> for the CORS field.</summary>
    private static List<string> DerivedCorsShow(BootstrapConfig c)
    {
        var snapshot = CloneForDerivation(c);
        ResolveDerivedDefaults(snapshot);
        return snapshot.ApiRuntime.CorsAllowedOrigins;
    }

    /// <summary>Throwaway snapshot for Show callbacks so derivation doesn't mutate live config.</summary>
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

    /// <summary>Never echoes the raw secret — supplements the field-level <c>Secret('*')</c> mask.</summary>
    private static string Mask(string secret) =>
        string.IsNullOrEmpty(secret) ? "<empty>" : "<set>";

    /// <summary>Renders empty as <c>&lt;empty&gt;</c> so operators can tell unset from missing.</summary>
    private static string ShowOrEmpty(string value) =>
        string.IsNullOrEmpty(value) ? "<empty>" : value;

    /// <summary>Wire spellings of every <see cref="ScyllaKeyspace"/>. Must stay aligned with
    /// <c>InterfoldAppHost.Configure</c>'s region list and <see cref="PublishPhase.BuildEnvReplacements"/>.</summary>
    internal static readonly string[] ValidScyllaKeyspaces = Enum
        .GetValues<ScyllaKeyspace>()
        .Select(k => k.ToWire())
        .ToArray();

    internal static readonly string[] ValidNodeGroups = Enum
        .GetValues<NodeGroup>()
        .Select(g => g.ToWire())
        .ToArray();

    internal static readonly string[] ValidDatabaseModes = Enum
        .GetValues<DatabaseMode>()
        .Select(m => m.ToWire())
        .ToArray();

    /// <summary>Must match <c>builder.AddContainer(...)</c> names in
    /// <c>InterfoldAppHost.Configure</c>; the regional entries mirror <see cref="ValidScyllaKeyspaces"/>.</summary>
    internal static readonly string[] ValidUpdateServices = ComposeServices.AllValidUpdateServices;

    private const int DefaultHttpPort = 80;
    private const int DefaultHttpsPort = 443;

    /// <summary>Fills empty <see cref="ApiRuntimeSection"/> fields from
    /// <see cref="DeploymentSection"/> + <see cref="PortsSection"/>. Non-empty values win;
    /// idempotent; mutates in place. Callback/JWT-authority always https (the API's Kestrel
    /// terminates TLS unconditionally). CORS uses the web tier's scheme + port. Called
    /// from <see cref="Validate"/> so JSON-load configs derive identically to prompted ones.</summary>
    internal static void ResolveDerivedDefaults(BootstrapConfig config)
    {
        if (config.Deployment.Hosts.Count == 0)
        {
            return;
        }

        // Silent on parse failure — this runs on every menu redraw (mid-edit is common);
        // Validate is the hard-fail path.
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
            }
        }

        var primary = HostParser.PickPrimary(parsed);
        if (primary is null)
        {
            // All CIDR / empty after filtering — Validate rejects later.
            return;
        }

        // Port suffix omitted at 443 so proxy-fronted stacks get clean URLs.
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
            // CORS follows the web tier's scheme + port; suffix omitted at 80/443.
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

    /// <summary>Throws on the first broken invariant with an operator-readable message.
    /// Internal for direct test-driven failure paths.</summary>
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

        // Compose binds each host port once; catch collisions here, not as
        // "port already allocated" mid-launch.
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

        // Postgres-safe identifier: quoting-free at both bind sites (connection string +
        // CREATE DATABASE) within the 63-byte NAMEDATALEN budget.
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

        // Intersection of what Cassandra's cassandra.yaml rewrite and Scylla's argv accept
        // without quoting gymnastics; 1..64 matches Cassandra's documented limit.
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

        // Derive first so JSON-load callers see the same post-derivation values as the form.
        ResolveDerivedDefaults(config);

        ValidateAbsoluteHttpUri(config.ApiRuntime.CallbackBaseUrl, "config.apiRuntime.callbackBaseUrl");
        ValidateAbsoluteHttpUri(config.ApiRuntime.JwtAuthority, "config.apiRuntime.jwtAuthority");

        if (string.IsNullOrWhiteSpace(config.ApiRuntime.JwtAudience))
        {
            throw new InvalidOperationException(
                "config.apiRuntime.jwtAudience must be a non-empty token-audience identifier (default: 'octocon').");
        }

        // WithOrigins() does exact string matching, so anything that doesn't round-trip
        // through Uri.TryCreate(http/https) can never match — reject upfront.
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

        // AvatarStorageRoot lives inside the API container — no host-side existence check.
        ValidateOptionalAbsoluteHttpUri(config.Storage.AvatarPublicBase, "config.storage.avatarPublicBase");
        ValidateOptionalAbsolutePath(
            config.Storage.AvatarStorageRoot,
            "config.storage.avatarStorageRoot",
            "must be an absolute path inside the API container (e.g. '/var/lib/interfold/avatars').");

        // http:// is legal for OTLP (SDK accepts gRPC-over-HTTP/2).
        ValidateOptionalAbsoluteHttpUri(config.Observability.OtlpEndpoint, "config.observability.otlpEndpoint");

        // Numeric bounds come from ConfigurationBounds so the bootstrapper prompts and the
        // API's [Range] attributes on the matching options stay lockstep.
        if (config.Socket.BatchBytesThreshold is { } socketThreshold)
        {
            ValidateIntRange(socketThreshold,
                ConfigurationBounds.SocketBatchBytesThresholdMin,
                ConfigurationBounds.SocketBatchBytesThresholdMax,
                "config.socket.batchBytesThreshold");
        }

        // Persistence tuning: max-vs-initial cross-check catches the easy inverted-values mistake.
        ValidateIntRange(config.Persistence.DbRetryAttempts,
            ConfigurationBounds.DbRetryAttemptsMin,
            ConfigurationBounds.DbRetryAttemptsMax,
            "config.persistence.dbRetryAttempts");
        ValidateIntRange(config.Persistence.DbRetryInitialDelayMs,
            ConfigurationBounds.DbRetryInitialDelayMsMin,
            ConfigurationBounds.DbRetryInitialDelayMsMax,
            "config.persistence.dbRetryInitialDelayMs");
        ValidateIntRange(config.Persistence.DbRetryMaxDelayMs,
            ConfigurationBounds.DbRetryMaxDelayMsMin,
            ConfigurationBounds.DbRetryMaxDelayMsMax,
                "config.persistence.dbRetryMaxDelayMs");
        // Cross-check catches the easy inverted-values mistake.
        if (config.Persistence.DbRetryMaxDelayMs < config.Persistence.DbRetryInitialDelayMs)
        {
            throw new InvalidOperationException(
                $"config.persistence.dbRetryMaxDelayMs ({config.Persistence.DbRetryMaxDelayMs}) " +
                $"must be >= dbRetryInitialDelayMs ({config.Persistence.DbRetryInitialDelayMs}).");
        }
        ValidateIntRange(config.Persistence.HydrationMaxConcurrency,
            ConfigurationBounds.HydrationMaxConcurrencyMin,
            ConfigurationBounds.HydrationMaxConcurrencyMax,
            "config.persistence.hydrationMaxConcurrency");

        // Real schedule grammar validation is `systemd-analyze calendar` at install time.
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
        ValidateOptionalAbsolutePath(
            config.Backup.Directory,
            "config.backup.directory",
            "must be an absolute path (systemd-driven backup invocations have an unpredictable CWD; " +
            "relative paths would not resolve consistently). Leave blank to default to '{outputDir}/backups'.");

        // Range covers realistic cold-starts (Postgres+Scylla+API ~60-120s on modest hardware);
        // 3600s cap matches UpdateImagesPhase's "give up eventually" contract.
        ValidateIntRange(
            config.Update.HealthCheckTimeoutSeconds, 1, 3600,
            "config.update.healthCheckTimeoutSeconds");

        // Empty = every service (valid default); only non-empty entries are checked.
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
    /// Accepts absolute http(s) URIs; rejects with the field name for a clear operator error.
    /// Delegates the actual scheme check to <see cref="AbsoluteHttpUriAttribute.IsAbsoluteHttpUri"/>
    /// so the API's <c>[AbsoluteHttpUri]</c> validation and this bootstrapper check share the
    /// same "is this an absolute http(s) URL" rule.
    /// </summary>
    private static void ValidateAbsoluteHttpUri(string value, string fieldLabel)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{fieldLabel} must be a non-empty http(s) URL.");
        }
        if (!AbsoluteHttpUriAttribute.IsAbsoluteHttpUri(value))
        {
            throw new InvalidOperationException(
                $"{fieldLabel}='{value}' is not a valid absolute http(s) URL.");
        }
    }

    /// <summary>Blank-legal sibling of <see cref="ValidateAbsoluteHttpUri"/>.</summary>
    private static void ValidateOptionalAbsoluteHttpUri(string value, string fieldLabel)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        ValidateAbsoluteHttpUri(value, fieldLabel);
    }

    /// <summary>
    /// Blank-legal absolute-path check. Delegates to
    /// <see cref="AbsolutePathAttribute.IsAbsolutePath"/> so the API's <c>[AbsolutePath]</c>
    /// validation and this bootstrapper check share the same rule.
    /// </summary>
    private static void ValidateOptionalAbsolutePath(string? value, string fieldLabel, string hint)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }
        if (!AbsolutePathAttribute.IsAbsolutePath(value))
        {
            throw new InvalidOperationException($"{fieldLabel}='{value}' {hint}");
        }
    }

    /// <summary>Shared int range check; error reports both field label and observed value.</summary>
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
    /// Character allow-list for <c>OnCalendar=</c>. Grammar validation is deferred to
    /// <c>systemd-analyze calendar</c> at install time; this only rejects obviously bogus
    /// input (control chars, shell metacharacters).
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
    /// Tier 1 of the two-tier mDNS design: pre-prompt banner. Detects the device hostname,
    /// probes <c>{hostname}.local</c>, prints a banner, and (interactive only) optionally
    /// installs avahi. Returns the hostname to seed "Public host(s)" with, or null when we
    /// should skip the pre-fill. Tier 2 is <see cref="ApplyMdnsGateAsync"/>.
    /// </summary>
    /// <param name="options">Non-interactive short-circuits (banner is TTY-only).</param>
    /// <param name="logger">Phase logger.</param>
    /// <param name="ct">Cancellation for probe + install.</param>
    /// <param name="hostnameFactory">Test seam; defaults to <see cref="HostnameDetector.TryDetectMdnsHostname"/>.</param>
    /// <param name="probe">Test seam; defaults to <see cref="MdnsAvailability.IsHostnameResolvableAsync"/>.</param>
    /// <param name="isInteractive">Test seam; defaults to <c>!Console.IsInputRedirected</c>.
    /// Provided because <see cref="Console.SetIn"/> in unit tests doesn't change
    /// <see cref="Console.IsInputRedirected"/> (the property is bound to the OS-level fd
    /// state, not the managed Console.In reader). Without this seam the unit test's
    /// StringReader stdin swap is invisible to the check and the install prompt fires
    /// whenever `dotnet test` runs from an interactive terminal.</param>
    internal static async Task<string?> ApplyPreFillMdnsCheckAsync(
        BootstrapOptions options,
        PhaseLogger logger,
        CancellationToken ct,
        Func<string?>? hostnameFactory = null,
        Func<string, CancellationToken, Task<bool?>>? probe = null,
        Func<bool>? isInteractive = null)
    {
        // Non-interactive skips the banner; the post-fill gate handles .local entries later.
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

        // mDNS unavailable — warn up-front, then (if we have a TTY) offer install.
        var distro = DistroInfo.Read();
        logger.Warn($"mDNS is unavailable on this device — {hostname} will NOT resolve on the LAN.");
        logger.Warn($"    to enable it: {MdnsAvailability.ManualInstallHint(distro.Family)}");

        // No TTY (test path lands here too) → skip install offer, operator gets the manual hint.
        var isInteractiveFn = isInteractive ?? (static () => !Console.IsInputRedirected);
        if (!isInteractiveFn())
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

        if (!await MdnsAvailability.TryInstallAvahiAsync(distro, logger, ct).ConfigureAwait(false))
        {
            logger.Warn($"    {hostname} will be omitted from the pre-fill");
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
    /// Tier 2 of the two-tier mDNS design: post-validation gate. Runs on the
    /// <see cref="BootstrapCommand.Bootstrap"/> command only. Probes every <c>.local</c> host;
    /// on failure offers install (interactive), then strips unresolvable entries with a
    /// warning. Bootstrap always continues — re-running to restore stripped entries is
    /// optional recovery.
    /// <para>
    /// Partial resolution is treated as broken: leaf-cert SAN chains are trust-all-or-nothing,
    /// so a smaller consistent set beats a broken half.
    /// </para>
    /// </summary>
    /// <returns>True when the hosts list was mutated (caller re-validates + re-persists).</returns>
    /// <param name="config">Bootstrap config; mutated in place when entries are stripped.</param>
    /// <param name="options">Drives the interactive-vs-strip fork.</param>
    /// <param name="logger">Phase logger.</param>
    /// <param name="ct">Cancellation for probe + install.</param>
    /// <param name="probe">Test seam; defaults to <see cref="MdnsAvailability.IsHostnameResolvableAsync"/>.</param>
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

        // Probe every .local individually (null result = can't probe on this platform).
        // Per-entry avoids masking an earlier broken host with a later resolvable one.
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

        // Interactive install offer — catches the "changed my mind" case after the banner
        // AND JSON-load runs that never saw the banner at all. Non-interactive skips it.
        if (!options.NonInteractive && !Console.IsInputRedirected)
        {
            var distro = DistroInfo.Read();
            var install = AnsiConsole.Prompt(new ConfirmationPrompt(
                $"mDNS is unavailable but your hosts list contains {string.Join(", ", broken)}. " +
                "Install avahi-daemon + nss-mdns now?")
            { DefaultValue = true });
            if (install)
            {
                if (await MdnsAvailability.TryInstallAvahiAsync(distro, logger, ct).ConfigureAwait(false))
                {
                    // Re-probe; any residual failure falls through to strip so the run still succeeds.
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
                else
                {
                    logger.Warn("removing unresolvable .local host(s) after failed avahi install.");
                }
            }
        }

        // Strip + warn + continue. Bootstrap doesn't halt; the install hint is recovery advice.
        var installHint = MdnsAvailability.ManualInstallHint(DistroInfo.Read().Family);
        logger.Warn($"removing unresolvable .local host(s) from config.deployment.hosts: {string.Join(", ", broken)}");
        logger.Warn($"    bootstrap will continue with the remaining hosts. To restore these entries on a future run, set up mDNS first: {installHint}");
        config.Deployment.Hosts = [.. config.Deployment.Hosts.Except(broken, StringComparer.OrdinalIgnoreCase)];
        return true;
    }
}
