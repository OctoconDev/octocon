using Aspire.Hosting;
using Interfold.AppHostGraph;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Contracts.Enums;
using Microsoft.Extensions.Configuration;
using Interfold.Contracts.Configuration;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// Phase 5 — invokes the embedded <see cref="DistributedApplication"/> in publish mode to emit
/// <c>docker-compose.yaml</c> + <c>.env</c> using the resource graph defined by
/// <c>InterfoldAppHost.Configure</c>. Generated secrets are injected through
/// <see cref="IConfiguration"/> so they end up in the <c>.env</c> file instead of leaking into
/// the compose YAML.
/// </summary>
internal static class PublishPhase
{
    /// <summary>
    /// Subdirectory name (two levels deep, created at runtime) used to anchor the relative bind-mount
    /// paths in <c>InterfoldAppHost.Configure</c>. The graph uses paths like
    /// <c>../../scripts/...</c> that resolve against the process CWD. Setting CWD to
    /// <c>{appDir}/_aspire_anchor/inner</c> makes <c>../../scripts</c> point at <c>{appDir}/scripts</c>,
    /// matching the dev layout (<c>csharp/Interfold.AppHost/../../scripts</c>).
    /// </summary>
    private static readonly string[] AnchorSegments = ["_aspire_anchor", "inner"];

    public static async Task RunAsync(
        BootstrapOptions options,
        BootstrapConfig config,
        GeneratedSecrets secrets,
        PhaseLogger logger,
        CancellationToken ct)
    {
        const string Phase = "publish";
        logger.PhaseStart(Phase);

        Directory.CreateDirectory(options.OutputDir);

        var anchor = SetupAnchor();
        var previousCwd = Directory.GetCurrentDirectory();

        try
        {
            Directory.SetCurrentDirectory(anchor);

            await PublishInProcessAsync(options, config, secrets, logger, ct).ConfigureAwait(false);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
        }

        var composePath = Path.Combine(options.OutputDir, BootstrapArtifactPaths.ComposeFileName);
        if (!File.Exists(composePath))
        {
            // Aspire >=13 sometimes emits to a subdirectory keyed by the environment name.
            // Look one level deeper before giving up.
            var nested = BootstrapArtifactPaths.FindComposeFile(options.OutputDir);
            if (nested is null)
            {
                logger.PhaseFail(Phase, PhaseFailureReasons.ComposeNotEmitted);
                throw new InvalidOperationException(
                    $"Aspire publish completed but no {BootstrapArtifactPaths.ComposeFileName} was produced under {options.OutputDir}.");
            }
            logger.Info($"    compose emitted at {nested}");
            composePath = nested;
        }

        // Aspire 13.x emits .env with empty values for every parameter and bind-mount source,
        // expecting the operator to fill them in before `docker compose up`. The bootstrapper IS
        // the operator, so we rewrite the file in place with the secrets we generated and the
        // bind-mount sources we anchored on the install directory.
        var envPath = Path.Combine(Path.GetDirectoryName(composePath)!, ".env");
        FillEnvFile(envPath, AppContext.BaseDirectory, options.OutputDir, config, secrets, logger);

        // Cassandra mode: stamp `pull_policy: never` onto the cassandra service in the emitted
        // compose YAML. This is a defensive no-op-on-pull marker, NOT the update mechanism —
        // updates to the cassandra image happen via a `docker build` inside
        // CassandraImagePhase.EnsureBuiltAsync (called from LaunchPhase, DatabaseInitPhase, and
        // UpdateImagesPhase BEFORE any compose lifecycle step). The stamp exists purely so
        // `docker compose pull` inside update-images doesn't crash on `interfold-cassandra:local`
        // — that tag is locally-built with no matching registry entry, so without the stamp
        // compose would attempt a registry fetch, exit non-zero on "manifest not found", and
        // halt update-images before the freshly-rebuilt image ID could feed into the post-pull
        // digest diff / `up -d` recreate. With the stamp, compose reports the cassandra service
        // as `Skipped` on pull (see pkg/compose/pull.go on docker/compose main, behaviour
        // merged in docker/compose#9376 back in 2022) and every other lifecycle step (`up`,
        // `build`, `restart`, ...) still runs against the locally-built image untouched.
        // Net result: cassandra DOES get updated on `update-images` runs — the update just
        // flows through `docker build` (from EnsureBuiltAsync) rather than `docker compose pull`.
        if (CassandraImagePhase.IsCassandraDeployment(config))
        {
            StampCassandraPullPolicyNever(composePath, logger);
        }

        logger.PhaseDone(Phase);
    }

    /// <summary>
    /// Pure, side-effect-free computation of the values we expect to substitute into the
    /// Aspire-emitted <c>.env</c>. Returns two dictionaries:
    /// <list type="bullet">
    ///   <item><c>Parameters</c> — keyed by the upper-snake-cased parameter name Aspire writes
    ///         (e.g. <c>POSTGRES_USER</c>), mapped to the secret/config value.</item>
    ///   <item><c>BindMounts</c> — keyed by the <c>service:container-target</c> identifier
    ///         emitted in the <c># Bind mount source for ...</c> comment above each bind-mount
    ///         placeholder, mapped to the absolute host path to substitute in.</item>
    /// </list>
    /// Splitting this out from <see cref="FillEnvFile"/> keeps the IO loop minimal and lets the
    /// unit-test project assert the full key set without staging a real <c>.env</c> on disk.
    /// </summary>
    internal sealed record EnvReplacements(
        IReadOnlyDictionary<string, string> Parameters,
        IReadOnlyDictionary<string, string> BindMounts);

    /// <summary>
    /// Translates the operator-facing <see cref="BootstrapConfig.DatabaseMode"/> enum into the
    /// orthogonal AppHost toggles that <c>InterfoldAppHost</c> reads. Keeping the enum in the
    /// operator-facing config lets <c>interfold.bootstrap.json</c> stay one value wide, while
    /// the resource graph stays driven by the same independent flags the integration tests use
    /// to spin Scylla and Cassandra side-by-side. <see cref="ConfigPhase.Validate"/> has already
    /// rejected any value outside this switch in normal flows; the throw guards internal callers
    /// (notably the unit tests) from silently bypassing validation.
    /// </summary>
    internal static (bool IncludeScylla, bool IncludeCassandra, ScyllaTopology ScyllaTopology) TranslateDatabaseMode(
        DatabaseMode databaseMode) => databaseMode switch
        {
            DatabaseMode.Single => (true, false, ScyllaTopology.Single),
            DatabaseMode.Multi => (true, false, ScyllaTopology.Multi),
            DatabaseMode.Cassandra => (false, true, ScyllaTopology.Single),
            _ => throw new InvalidOperationException(
                $"Unhandled databaseMode '{databaseMode}'. Expected: single | multi | cassandra."),
        };

    /// <summary>
    /// Single source of truth for every operator-tunable Aspire parameter that has to appear in
    /// BOTH places the publish pipeline reads: (1) the <c>.env</c> replacement dictionary built by
    /// <see cref="BuildEnvReplacements"/>, and (2) the <c>Parameters:*</c> injection dictionary
    /// built by <c>PublishInProcessAsync</c>. Each entry returns the paired keys and the value:
    /// <list type="bullet">
    ///   <item><c>ConfigKey</c> — the <c>Parameters:*</c> IConfiguration key, i.e. the value
    ///         <see cref="AppHostParameterKeys"/> exposes; consumed by
    ///         <c>InterfoldAppHost.Configure</c> via <c>AddParameter</c> calls.</item>
    ///   <item><c>EnvKey</c> — the upper-snake-cased <c>.env</c> spelling Aspire emits for that
    ///         parameter (e.g. <c>Parameters:postgres-user</c> ↔ <c>POSTGRES_USER</c>). Kept
    ///         explicit rather than derived so a future Aspire rename can be handled with a
    ///         one-line edit here instead of a scattered code hunt; the round-trip is asserted
    ///         in <c>PublishSharedAspireParametersTests</c>.</item>
    ///   <item><c>Value</c> — the concrete secret/config value that both dictionaries need.
    ///         Nullable operator inputs (avatar paths, OTLP endpoint, socket-batch threshold)
    ///         collapse to <see cref="string.Empty"/> here to reproduce the "env var unset" branch
    ///         the API side takes on read (see <c>ApplyStorage</c> / <c>ApplyObservability</c> /
    ///         <c>TryParseInt</c>).</item>
    /// </list>
    ///
    /// <para>
    /// <b>Deliberately absent</b> from this enumerator:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>ClusterName</c>, <c>IncludeScylla</c>, <c>IncludeCassandra</c>, <c>ScyllaTopology</c>,
    ///         <c>ApiImage</c>, <c>IncludeDashboard</c>, <c>IncludeWeb</c>, <c>WebTls</c>,
    ///         <c>WebServerName</c>, and every <c>Ports:*</c> key — graph-only knobs consumed at
    ///         <c>InterfoldAppHost.Configure</c> time; they don't round-trip through <c>.env</c>
    ///         and therefore have no matching upper-snake key.</item>
    ///   <item><c>CASSANDRA_IMAGE</c> — env-only artefact; the value flows in through
    ///         <see cref="CassandraImagePhase"/>, not <see cref="BootstrapConfig"/>.</item>
    /// </list>
    /// </summary>
    internal static IEnumerable<(string ConfigKey, string EnvKey, string Value)>
        EnumerateSharedAspireParameters(BootstrapConfig config, GeneratedSecrets secrets)
    {
        // Postgres credentials + database name. POSTGRES_INIT_PASSWORD deliberately carries the
        // *initial* value (matches what initdb sets on a fresh db_init role) so operators who nuke
        // pgdata can rerun the bootstrap; DatabaseInitPhase scrambles it again in-cluster after use.
        yield return (AppHostParameterKeys.PostgresUser, "POSTGRES_USER", secrets.PostgresUser);
        yield return (AppHostParameterKeys.PostgresPassword, "POSTGRES_PASSWORD", secrets.PostgresPassword);
        yield return (AppHostParameterKeys.PostgresDb, "POSTGRES_DB", config.PostgresDatabase);
        yield return (AppHostParameterKeys.PostgresInitPassword, "POSTGRES_INIT_PASSWORD", secrets.PostgresInitPassword);

        // Scylla app-role credentials. SCYLLA_ADMIN_PASSWORD is intentionally absent — the admin
        // role is created by DatabaseInitPhase with a fresh random password that lives only in
        // internal.secrets, never in the compose .env.
        yield return (AppHostParameterKeys.ScyllaUser, "SCYLLA_USER", secrets.ScyllaUser);
        yield return (AppHostParameterKeys.ScyllaPassword, "SCYLLA_PASSWORD", secrets.ScyllaPassword);

        // Encryption bootstrapping key material. The encryption pepper, OAuth client secrets, JWT
        // signing keys, deep-link HMAC secret, and leaf PFX password all live in internal.secrets
        // exclusively (seeded by DatabaseInitPhase) — the API reads them through
        // SecretsBootstrapService / a one-shot Npgsql query at startup, not from .env.
        yield return (AppHostParameterKeys.EncryptionPrivateKey, "ENCRYPTION_PRIVATE_KEY", secrets.EncryptionPrivateKeyB64);

        // OAuth client IDs — public per-provider identifiers (NOT secrets) that end up in each
        // scheme's authorize redirect URL and are paired with the matching client secrets that
        // live in internal.secrets. Empty is a valid "I'm not using this provider" signal (the
        // API's scheme registrar skips empty IDs).
        yield return (AppHostParameterKeys.GoogleOAuthClientId, "GOOGLE_OAUTH_CLIENT_ID", config.OAuth.GoogleClientId ?? string.Empty);
        yield return (AppHostParameterKeys.DiscordOAuthClientId, "DISCORD_OAUTH_CLIENT_ID", config.OAuth.DiscordClientId ?? string.Empty);
        yield return (AppHostParameterKeys.AppleOAuthClientId, "APPLE_OAUTH_CLIENT_ID", config.OAuth.AppleClientId ?? string.Empty);

        // API runtime config. All five are non-secret plain-text values the API container consumes
        // as OCTOCON_* env vars. ConfigPhase.ResolveDerivedDefaults fills empties before Validate
        // runs, so by publish time every value here is guaranteed non-empty (CORS list joined with
        // commas to match OCTOCON_CORS_ALLOWED_ORIGINS' wire format).
        yield return (AppHostParameterKeys.ScyllaKeyspace, "SCYLLA_KEYSPACE", config.ScyllaKeyspace.ToWire());
        yield return (AppHostParameterKeys.OAuthCallbackBaseUrl, "OAUTH_CALLBACK_BASE_URL", config.ApiRuntime.CallbackBaseUrl);
        yield return (AppHostParameterKeys.JwtAuthority, "JWT_AUTHORITY", config.ApiRuntime.JwtAuthority);
        yield return (AppHostParameterKeys.JwtAudience, "JWT_AUDIENCE", config.ApiRuntime.JwtAudience);
        yield return (AppHostParameterKeys.CorsAllowedOrigins, "CORS_ALLOWED_ORIGINS", string.Join(",", config.ApiRuntime.CorsAllowedOrigins));

        // Operator tuning knobs (cluster / storage / observability / socket / persistence). All
        // nine are non-secret. The four nullable / disabled-when-empty fields (AvatarStorageRoot,
        // AvatarPublicBase, OtlpEndpoint, BatchBytesThreshold) serialise empty/null as the empty
        // string — the API's ApplyStorage / ApplyObservability binders and TryParseInt all
        // normalise empty → null, reproducing the "env var unset" behaviour 1:1.
        yield return (AppHostParameterKeys.NodeGroup, "NODE_GROUP", config.Cluster.NodeGroup.ToWire());
        yield return (AppHostParameterKeys.AvatarStorageRoot, "AVATAR_STORAGE_ROOT", config.Storage.AvatarStorageRoot ?? string.Empty);
        yield return (AppHostParameterKeys.AvatarPublicBase, "AVATAR_PUBLIC_BASE", config.Storage.AvatarPublicBase ?? string.Empty);
        yield return (AppHostParameterKeys.OtlpEndpoint, "OTLP_ENDPOINT", config.Observability.OtlpEndpoint ?? string.Empty);
        yield return (AppHostParameterKeys.SocketBatchBytesThreshold, "SOCKET_BATCH_BYTES_THRESHOLD", config.Socket.BatchBytesThreshold?.ToString() ?? string.Empty);
        yield return (AppHostParameterKeys.DbRetryAttempts, "DB_RETRY_ATTEMPTS", config.Persistence.DbRetryAttempts.ToString());
        yield return (AppHostParameterKeys.DbRetryInitialDelayMs, "DB_RETRY_INITIAL_DELAY_MS", config.Persistence.DbRetryInitialDelayMs.ToString());
        yield return (AppHostParameterKeys.DbRetryMaxDelayMs, "DB_RETRY_MAX_DELAY_MS", config.Persistence.DbRetryMaxDelayMs.ToString());
        yield return (AppHostParameterKeys.HydrationMaxConcurrency, "HYDRATION_MAX_CONCURRENCY", config.Persistence.HydrationMaxConcurrency.ToString());
    }

    internal static EnvReplacements BuildEnvReplacements(
        BootstrapConfig config,
        GeneratedSecrets secrets,
        string baseDir,
        string outputDir)
    {
        // Seed from the shared enumerator so every operator-tunable parameter is emitted here
        // AND injected as an AppHost Parameter (see PublishInProcessAsync) from a single source
        // of truth. See EnumerateSharedAspireParameters for the full docstring covering:
        //   - why postgres-init-password / oauth-client-id / cluster-name each land where they do
        //   - the kebab-case ↔ upper-snake-case pairing (EnvKey is the Aspire-derived .env spelling)
        //   - the nullable-tunable ??  string.Empty carve-outs
        var parameters = EnumerateSharedAspireParameters(config, secrets)
            .ToDictionary(p => p.EnvKey, p => p.Value, StringComparer.Ordinal);

        if (CassandraImagePhase.IsCassandraDeployment(config))
        {
            // Aspire's compose publisher emits `image: "${CASSANDRA_IMAGE}"` for AddDockerfile
            // resources. Without this entry the cassandra service starts with an empty image ref
            // and docker compose fails before DatabaseInitPhase can seed roles.
            parameters["CASSANDRA_IMAGE"] = CassandraImagePhase.LocalImageTag;
        }

        // Bind-mount source resolution. Aspire emits a comment of the form
        //   # Bind mount source for <service>:<container-target-path>
        // immediately above each bind-mount placeholder. We use that "service:target" key to
        // look up the absolute host path. If Aspire renames or re-orders these in a future
        // release, this map needs to track the change - we'd notice via the test suite.
        // The lookup mirrors the WithBindMount() calls in InterfoldAppHost.Configure().
        var bindMountLookup = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Bootstrapper-issued TLS material lives under {outputDir}/certs/. Kestrel inside
            // the API container reads leaf.pfx via the path env var set in ConfigureApiSelfHostEnv.
            // The JWT signing PEMs that used to live under secrets/keys/ are now part of
            // internal.secrets (see SeedKeys.cs) — no bind mount needed.
            [$"{ComposeServices.InterfoldApi}:/certs"] = Path.Combine(outputDir, "certs"),
        };

        // When the operator opts in to web TLS, InterfoldAppHost.Configure adds two extra
        // bind mounts on the octocon-web service: the cert directory (re-using the API's
        // {outputDir}/certs/) and an envsubst template that the official nginx image renders
        // into /etc/nginx/conf.d/ at startup. The template ships alongside the bootstrapper
        // binary under {baseDir}/web/nginx/ (extracted by EmbeddedSupportFiles on first run;
        // BootstrapperBuild also stages it for the integration tests).
        if (config.Deployment.WebHttps)
        {
            bindMountLookup[$"{ComposeServices.OctoconWeb}:/certs"] = Path.Combine(outputDir, "certs");
            bindMountLookup[$"{ComposeServices.OctoconWeb}:/etc/nginx/templates/default.conf.template"] =
                Path.Combine(baseDir, "web", "nginx", "default.conf.template");
        }

        // Scylla rackdc.properties bind mount is region-keyed. Single mode uses one node named
        // "scylla" with the "nam" region (default); multi mode emits one node per region.
        // The region list mirrors InterfoldAppHost.Configure(), derived from the ScyllaKeyspace
        // enum so it can't drift from the typed region vocabulary.
        string[] scyllaRegions = config.DatabaseMode == DatabaseMode.Multi
            ? Enum.GetValues<ScyllaKeyspace>().Select(k => k.ToWire()).ToArray()
            : [ScyllaKeyspace.Nam.ToWire()];
        foreach (var region in scyllaRegions)
        {
            var nodeName = ComposeServices.ToScyllaNodeName(region, multiNode: scyllaRegions.Length > 1);
            bindMountLookup[$"{nodeName}:/etc/scylla/cassandra-rackdc.properties"] =
                Path.Combine(baseDir, "db", "scylla", $"cassandra-rackdc.{region}.properties");
        }

        return new EnvReplacements(parameters, bindMountLookup);
    }

    /// <summary>
    /// Rewrites the Aspire-emitted <c>.env</c> with concrete values for every secret parameter
    /// and bind-mount source placeholder. Unknown keys are left untouched so future additions in
    /// <see cref="InterfoldAppHost.Configure"/> degrade gracefully (the operator will see the
    /// blank key and we'll know to teach this method about it).
    /// </summary>
    private static void FillEnvFile(
        string envPath,
        string baseDir,
        string outputDir,
        BootstrapConfig config,
        GeneratedSecrets secrets,
        PhaseLogger logger)
    {
        if (!File.Exists(envPath))
        {
            logger.Warn($".env not found at {envPath}; skipping value rewrite");
            return;
        }

        var replacements = BuildEnvReplacements(config, secrets, baseDir, outputDir);
        var (rewritten, skipped) = ApplyReplacementsToEnvFile(envPath, replacements);

        logger.Info($"    .env: filled {rewritten} value(s)");
        if (skipped.Count > 0)
        {
            logger.Warn($".env: {skipped.Count} key(s) left blank: {string.Join(", ", skipped)}");
        }
    }

    /// <summary>
    /// Reads <paramref name="envPath"/>, applies <paramref name="replacements"/> in place, and
    /// rewrites the file. Returns the number of rewritten lines plus the list of <c>KEY=</c>
    /// entries left blank because we had no replacement for them. Internal because the unit-test
    /// project drives it directly against an in-memory .env to exercise the comment-pair logic.
    /// </summary>
    internal static (int Rewritten, IReadOnlyList<string> Skipped) ApplyReplacementsToEnvFile(
        string envPath, EnvReplacements replacements)
    {
        var lines = File.ReadAllLines(envPath);
        string? pendingBindMountKey = null;
        var rewritten = 0;
        var skipped = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Track the most recent comment so we can pair it with the following `KEY=` line.
            if (line.StartsWith("# Bind mount source for ", StringComparison.Ordinal))
            {
                pendingBindMountKey = line["# Bind mount source for ".Length..].Trim();
                continue;
            }

            // KEY=VALUE substitution. Aspire writes blank RHS in publish mode.
            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                pendingBindMountKey = null;
                continue;
            }

            var key = line[..eq];
            string? value = null;

            if (pendingBindMountKey is not null && replacements.BindMounts.TryGetValue(pendingBindMountKey, out var src))
            {
                value = src;
            }
            else if (replacements.Parameters.TryGetValue(key, out var paramVal))
            {
                value = paramVal;
            }

            if (value is not null)
            {
                lines[i] = $"{key}={value}";
                rewritten++;
            }
            else if (line.EndsWith('='))
            {
                // A blank value that we did NOT recognise. Surface it - operator may need to
                // fix it before `docker compose up`. (Historically the DISCORD_* prefix was
                // excluded because DISCORD_OAUTH_CLIENT_SECRET was the only optional Aspire
                // parameter that Aspire would emit unfilled; the secret moved to
                // internal.secrets and the matching CLIENT_ID is now explicitly written via
                // BuildEnvReplacements with an empty string when the operator didn't supply
                // one, so the carve-out is no longer needed.)
                skipped.Add(key);
            }

            pendingBindMountKey = null;
        }

        File.WriteAllLines(envPath, lines);
        return (rewritten, skipped);
    }

    /// <summary>
    /// Inserts a <c>pull_policy: never</c> line immediately after the
    /// <c>image: "${CASSANDRA_IMAGE}"</c> line on the cassandra service, if not already
    /// present. Idempotent: re-runs are no-ops.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uses the exact <c>${CASSANDRA_IMAGE}</c> token as the anchor — that placeholder is
    /// unique to the cassandra service in the compose file emitted by Aspire's
    /// <c>AddDockerfile("cassandra", ...)</c> resource, so we never accidentally stamp
    /// another service. A targeted find+insert is safer here than a full YAML parse/serialise
    /// round-trip that could re-order Aspire's other keys (bind mounts, network aliases,
    /// depends_on) or reflow the anchors compose relies on.
    /// </para>
    /// <para>
    /// The three passes of <see cref="File.ReadAllLines(string)"/> +
    /// <see cref="File.WriteAllLines(string, IEnumerable{string})"/> preserve the file's
    /// existing line endings on the compose YAML: read via System.IO's newline-agnostic
    /// splitter, write with the platform default. Linux hosts (the only cassandra deployment
    /// target — the whole stack runs under docker compose on Linux) round-trip as LF.
    /// </para>
    /// </remarks>
    internal static void StampCassandraPullPolicyNever(string composePath, PhaseLogger? logger = null)
    {
        var lines = File.ReadAllLines(composePath).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (!line.Contains("${CASSANDRA_IMAGE}", StringComparison.Ordinal)) continue;

            // Idempotency guard: bail out if the next non-blank line already asserts the
            // policy (re-runs of `bootstrap publish` must not double-stamp).
            var next = lines.Skip(i + 1).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            if (string.Equals(next?.Trim(), "pull_policy: never", StringComparison.Ordinal))
            {
                logger?.Info("    compose: cassandra service already has pull_policy: never");
                return;
            }

            var indent = line[..(line.Length - line.TrimStart().Length)];
            lines.Insert(i + 1, $"{indent}pull_policy: never");
            File.WriteAllLines(composePath, lines);
            logger?.Info("    compose: stamped pull_policy: never on cassandra service");
            return;
        }
        // No match = no cassandra service in the compose file. Older cassandra-mode setups
        // that never went through the AddDockerfile flow don't need the stamp; silently
        // accept as a no-op rather than throwing.
        logger?.Warn("compose: no ${CASSANDRA_IMAGE} anchor found; pull_policy stamp skipped");
    }

    private static string SetupAnchor()
    {
        // AppContext.BaseDirectory is where the published binary lives (e.g. /opt/interfold-bootstrap/).
        // Anchor is the relative working dir Aspire's compose publisher runs against; the
        // bind-mount source files (Scylla rackdc properties, nginx envsubst template) are
        // guaranteed to be on disk under baseDir by Orchestrator.RunAsync's call to
        // EmbeddedSupportFiles.EnsureExtracted, so this method only has to ensure the anchor
        // directory exists.
        var baseDir = AppContext.BaseDirectory;
        var anchor = Path.Combine(baseDir, Path.Combine(AnchorSegments));
        Directory.CreateDirectory(anchor);
        return anchor;
    }

    private static async Task PublishInProcessAsync(
        BootstrapOptions options,
        BootstrapConfig config,
        GeneratedSecrets secrets,
        PhaseLogger logger,
        CancellationToken ct)
    {
        // Aspire 13.x publish CLI passes `--operation publish --step publish --output-path X`
        // (see src/Aspire.Cli/Commands/PublishCommand.cs). Without `--step publish` the AppHost
        // only emits aspire-manifest.json and then blocks waiting for the CLI backchannel to
        // dispatch the next step, which never arrives because we are not running under the CLI.
        // The docker-compose publisher is selected by the registered AddDockerComposeEnvironment
        // in InterfoldAppHost.Configure, not by an explicit `--publisher` arg in 13.x.
        var publishArgs = new[]
        {
            "--operation", "publish",
            "--step", "publish",
            "--output-path", options.OutputDir,
        };

        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = publishArgs,
            // Headless: no dashboard, no browser launch - we just want the artifact emitter to run and exit.
            DisableDashboard = true,
        });

        var (includeScylla, includeCassandra, scyllaTopology) = TranslateDatabaseMode(config.DatabaseMode);

        // Inject all Parameters:* into the in-memory config layer. The graph's AddParameter() calls and
        // default-password guards read from IConfiguration; once published, Aspire writes secret parameter
        // values into the sibling .env file rather than embedding them in the compose YAML.
        //
        // Seed from the shared enumerator — every operator-tunable value that appears BOTH here and in
        // BuildEnvReplacements lives in EnumerateSharedAspireParameters. The extras appended below are
        // graph-only knobs that never round-trip through .env (topology toggles, image tag, port
        // mappings, cluster name) and therefore don't have a matching upper-snake key.
        var injected = EnumerateSharedAspireParameters(config, secrets)
            .ToDictionary(p => p.ConfigKey, p => (string?)p.Value, StringComparer.Ordinal);

        // ClusterName is read directly from IConfiguration in InterfoldAppHost (matching the
        // include-scylla / scylla-topology pattern) rather than as an Aspire parameter resource,
        // because the value also has to land on Scylla's WithArgs list and that overload takes
        // plain strings. The value gets baked into the compose YAML at publish time as both
        // CASSANDRA_CLUSTER_NAME and --cluster-name; no .env round trip needed.
        injected[AppHostParameterKeys.ClusterName] = config.ClusterName;
        injected[AppHostParameterKeys.IncludeScylla] = BoolWire.ToWireValue(includeScylla);
        injected[AppHostParameterKeys.IncludeCassandra] = BoolWire.ToWireValue(includeCassandra);
        injected[AppHostParameterKeys.ScyllaTopology] = scyllaTopology.ToWireValue();
        // The bootstrapper never builds the API from source — point Aspire at the pre-built image
        // so it emits a compose service referencing that tag directly. See InterfoldAppHost.Configure
        // for how this switches off the AddProject<> code path.
        injected[AppHostParameterKeys.ApiImage] = config.ApiImage;
        // Self-hosting stacks don't need the Aspire dev dashboard - it would pull an MCR-nightly
        // image at compose-up time which is inappropriate for production deployments.
        injected[AppHostParameterKeys.IncludeDashboard] = BoolWire.FalseValue;
        // The web container is opt-in via either of two independent toggles:
        //   * `deployment.includeWeb=true` → ship the octocon-web container HTTP-only.
        //   * `deployment.webHttps=true` → ship the container AND terminate TLS at it.
        // The second implies the first (cert wiring is useless without the container that
        // reads it), so we OR the two flags into Parameters:include-web. Parameters:web-tls
        // remains driven by webHttps alone — operators who only flip includeWeb get an
        // HTTP-only octocon-web for debugging / external-TLS-proxy stacks.
        injected[AppHostParameterKeys.IncludeWeb] = BoolWire.ToWireValue(config.Deployment.IncludeWeb || config.Deployment.WebHttps);
        injected[AppHostParameterKeys.WebTls] = BoolWire.ToWireValue(config.Deployment.WebHttps);
        // Server name baked into the rendered nginx config. nginx accepts DNS names and bare
        // IP literals as server_name but does NOT accept CIDR notation, so we use the first
        // non-CIDR host (the same "primary host" rule ConfigPhase.ResolveDerivedDefaults uses
        // for callback URL derivation). ConfigPhase.Validate guarantees at least one
        // leaf-eligible entry exists; the `_` catch-all fallback only kicks in for the
        // bypass-validation dev path that goes straight to InterfoldAppHost.Configure without
        // running the bootstrapper.
        injected[AppHostParameterKeys.WebServerName] = PickServerName(config.Deployment.Hosts);
        injected[AppHostParameterKeys.PortsPostgres] = config.Ports.Postgres.ToString();
        injected[AppHostParameterKeys.PortsScylla] = config.Ports.Scylla.ToString();
        injected[AppHostParameterKeys.PortsApiHttp] = config.Ports.ApiHttp.ToString();
        injected[AppHostParameterKeys.PortsApiHttps] = config.Ports.ApiHttps.ToString();
        injected[AppHostParameterKeys.PortsWebHttp] = config.Ports.WebHttp.ToString();
        injected[AppHostParameterKeys.PortsWebHttps] = config.Ports.WebHttps.ToString();

        builder.Configuration.AddInMemoryCollection(injected);

        InterfoldAppHost.Configure(builder);

        logger.Info("    invoking Aspire publish...");
        await using var app = builder.Build();
        await app.RunAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Selects the nginx <c>server_name</c> from a raw <see cref="DeploymentSection.Hosts"/>
    /// list: first parseable, non-CIDR entry wins. Falls back to <c>_</c> (the nginx catch-all)
    /// when no leaf-eligible entry exists — covers a defensive path that should be unreachable
    /// in practice because <see cref="ConfigPhase.Validate"/> rejects an all-CIDR-or-empty list
    /// before this method runs.
    /// </summary>
    internal static string PickServerName(IReadOnlyList<string> hosts)
    {
        foreach (var raw in hosts)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            HostEntry entry;
            try
            {
                entry = HostParser.Parse(raw);
            }
            catch (FormatException)
            {
                continue;
            }
            if (!entry.IsLeafEligible) continue;
            return entry.Kind switch
            {
                HostKind.Dns => entry.DnsName!,
                _ => entry.Ip!.ToString(),
            };
        }
        return "_";
    }
}
