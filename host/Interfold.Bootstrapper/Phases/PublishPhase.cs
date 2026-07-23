using Aspire.Hosting;
using Interfold.AppHostGraph;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Shared.Contracts.Enums;
using Microsoft.Extensions.Configuration;
using Interfold.Shared.Contracts.Configuration;

namespace Interfold.Bootstrapper.Phases;

/// <summary>Phase 5 — runs the embedded <see cref="DistributedApplication"/> in publish mode
/// to emit <c>docker-compose.yaml</c> + <c>.env</c> from <c>InterfoldAppHost.Configure</c>.
/// Generated secrets flow via <see cref="IConfiguration"/> so they land in <c>.env</c>, not the
/// compose YAML.</summary>
internal static class PublishPhase
{
    // Anchors relative bind-mount paths from InterfoldAppHost.Configure. The graph uses
    // "../../scripts/..." that resolves against CWD; setting CWD to {appDir}/_aspire_anchor/inner
    // makes those match the dev layout (host/Interfold.AppHost/../../scripts).
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
            // Aspire >=13 sometimes emits into a subdirectory keyed by the environment name.
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

        // Aspire 13.x emits .env with blank RHS for every parameter and bind-mount source,
        // expecting the operator to fill them. The bootstrapper IS the operator.
        var envPath = Path.Combine(Path.GetDirectoryName(composePath)!, ".env");
        FillEnvFile(envPath, AppContext.BaseDirectory, options.OutputDir, config, secrets, logger);

        // Cassandra mode: stamp `pull_policy: never` on the cassandra service. This is NOT the
        // update mechanism (CassandraImagePhase.EnsureBuiltAsync runs `docker build` before any
        // compose step); it prevents `docker compose pull` in update-images from erroring on
        // `interfold-cassandra:local` (no registry entry → "manifest not found" → non-zero exit
        // before the digest diff runs). With the stamp compose reports cassandra as Skipped on
        // pull (docker/compose#9376) and every other lifecycle step still targets the local build.
        if (CassandraImagePhase.IsCassandraDeployment(config))
        {
            StampCassandraPullPolicyNever(composePath, logger);
        }

        logger.PhaseDone(Phase);
    }

    /// <summary>Values to splice into the Aspire-emitted <c>.env</c>.
    /// <c>Parameters</c>: keyed by upper-snake env name (e.g. <c>POSTGRES_USER</c>).
    /// <c>BindMounts</c>: keyed by the <c>service:container-target</c> identifier from the
    /// <c># Bind mount source for ...</c> comment above each placeholder. Separated from
    /// <see cref="FillEnvFile"/> so unit tests can assert the key set without staging a real .env.</summary>
    internal sealed record EnvReplacements(
        IReadOnlyDictionary<string, string> Parameters,
        IReadOnlyDictionary<string, string> BindMounts);

    /// <summary>Translates the operator-facing <see cref="BootstrapConfig.DatabaseMode"/> enum
    /// into the orthogonal AppHost toggles <c>InterfoldAppHost</c> reads. The throw guards
    /// internal callers (unit tests) that bypass <see cref="ConfigPhase.Validate"/>.</summary>
    internal static (bool IncludeScylla, bool IncludeCassandra, ScyllaTopology ScyllaTopology) TranslateDatabaseMode(
        DatabaseMode databaseMode) => databaseMode switch
        {
            DatabaseMode.Single => (true, false, ScyllaTopology.Single),
            DatabaseMode.Multi => (true, false, ScyllaTopology.Multi),
            DatabaseMode.Cassandra => (false, true, ScyllaTopology.Single),
            _ => throw new InvalidOperationException(
                $"Unhandled databaseMode '{databaseMode}'. Expected: single | multi | cassandra."),
        };

    /// <summary>Single source of truth for every operator-tunable Aspire parameter that must
    /// appear in BOTH the <c>.env</c> replacement dictionary (<see cref="BuildEnvReplacements"/>)
    /// AND the <c>Parameters:*</c> injection dictionary (<c>PublishInProcessAsync</c>).
    /// <c>ConfigKey</c> is the <see cref="AppHostParameterKeys"/> IConfiguration key.
    /// <c>EnvKey</c> is the upper-snake spelling Aspire emits into .env; explicit (not derived) so
    /// an Aspire rename is a one-line edit — round-trip asserted in
    /// <c>PublishSharedAspireParametersTests</c>. Nullable inputs collapse to empty to reproduce
    /// the API's "unset env var" branch. Absent by design: graph-only knobs
    /// (cluster/topology/image/dashboard/web/ports) that don't round-trip through .env, plus
    /// <c>CASSANDRA_IMAGE</c> which flows in via <see cref="CassandraImagePhase"/>.</summary>
    internal static IEnumerable<(string ConfigKey, string EnvKey, string Value)>
        EnumerateSharedAspireParameters(BootstrapConfig config, GeneratedSecrets secrets)
    {
        // POSTGRES_INIT_PASSWORD carries the *initial* db_init password so operators who nuke
        // pgdata can rerun the bootstrap; DatabaseInitPhase scrambles it again in-cluster.
        yield return (AppHostParameterKeys.PostgresUser, "POSTGRES_USER", secrets.PostgresUser);
        yield return (AppHostParameterKeys.PostgresPassword, "POSTGRES_PASSWORD", secrets.PostgresPassword);
        yield return (AppHostParameterKeys.PostgresDb, "POSTGRES_DB", config.PostgresDatabase);
        yield return (AppHostParameterKeys.PostgresInitPassword, "POSTGRES_INIT_PASSWORD", secrets.PostgresInitPassword);

        // Scylla admin password is intentionally absent — that role is created by
        // DatabaseInitPhase and its password only lives in internal.secrets.
        yield return (AppHostParameterKeys.ScyllaUser, "SCYLLA_USER", secrets.ScyllaUser);
        yield return (AppHostParameterKeys.ScyllaPassword, "SCYLLA_PASSWORD", secrets.ScyllaPassword);

        // Bootstrap encryption key only. All other secrets (OAuth client secrets, JWT signing keys,
        // deep-link HMAC, leaf PFX password, encryption pepper) live in internal.secrets, seeded
        // by DatabaseInitPhase and read via SecretsBootstrapService at startup.
        yield return (AppHostParameterKeys.EncryptionPrivateKey, "ENCRYPTION_PRIVATE_KEY", secrets.EncryptionPrivateKeyB64);

        // OAuth client IDs are public identifiers (not secrets); empty is a valid
        // "provider disabled" signal — the API's scheme registrar skips empty IDs.
        yield return (AppHostParameterKeys.GoogleOAuthClientId, "GOOGLE_OAUTH_CLIENT_ID", config.OAuth.GoogleClientId ?? string.Empty);
        yield return (AppHostParameterKeys.DiscordOAuthClientId, "DISCORD_OAUTH_CLIENT_ID", config.OAuth.DiscordClientId ?? string.Empty);
        yield return (AppHostParameterKeys.AppleOAuthClientId, "APPLE_OAUTH_CLIENT_ID", config.OAuth.AppleClientId ?? string.Empty);

        // API runtime config → OCTOCON_* env vars. ConfigPhase.ResolveDerivedDefaults fills
        // empties before Validate; CORS list joined with commas to match the wire format.
        yield return (AppHostParameterKeys.ScyllaKeyspace, "SCYLLA_KEYSPACE", config.ScyllaKeyspace.ToWire());
        yield return (AppHostParameterKeys.OAuthCallbackBaseUrl, "OAUTH_CALLBACK_BASE_URL", config.ApiRuntime.CallbackBaseUrl);
        yield return (AppHostParameterKeys.JwtAuthority, "JWT_AUTHORITY", config.ApiRuntime.JwtAuthority);
        yield return (AppHostParameterKeys.JwtAudience, "JWT_AUDIENCE", config.ApiRuntime.JwtAudience);
        yield return (AppHostParameterKeys.CorsAllowedOrigins, "CORS_ALLOWED_ORIGINS", string.Join(",", config.ApiRuntime.CorsAllowedOrigins));

        // Non-secret operator tuning knobs. Nullable/disabled-when-empty fields serialise as
        // "" — the API's binders normalise empty → null, matching the "env var unset" branch.
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
        // Seeded from the shared enumerator so every operator-tunable value is emitted here AND
        // injected as an AppHost Parameter from one source of truth.
        var parameters = EnumerateSharedAspireParameters(config, secrets)
            .ToDictionary(p => p.EnvKey, p => p.Value, StringComparer.Ordinal);

        if (CassandraImagePhase.IsCassandraDeployment(config))
        {
            // Aspire emits `image: "${CASSANDRA_IMAGE}"` for AddDockerfile resources;
            // without this entry compose starts the service with an empty image ref.
            parameters["CASSANDRA_IMAGE"] = CassandraImagePhase.LocalImageTag;
        }

        // Bind-mount lookup. Aspire emits `# Bind mount source for <service>:<target>` above each
        // placeholder; we key on "service:target" to resolve the host path. A rename or reorder
        // in a future Aspire release surfaces via the unit tests.
        var bindMountLookup = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // TLS material lives under {outputDir}/certs/; Kestrel reads leaf.pfx via
            // ConfigureApiSelfHostEnv. JWT signing PEMs are in internal.secrets, no mount needed.
            [$"{ComposeServices.InterfoldApi}:/certs"] = Path.Combine(outputDir, "certs"),
        };

        // Web-TLS opt-in mounts: cert dir (reused from the API) + envsubst template. The template
        // ships under {baseDir}/web/nginx/ (extracted by EmbeddedSupportFiles / staged by
        // BootstrapperBuild for integration tests).
        if (config.Deployment.WebHttps)
        {
            bindMountLookup[$"{ComposeServices.OctoconWeb}:/certs"] = Path.Combine(outputDir, "certs");
            bindMountLookup[$"{ComposeServices.OctoconWeb}:/etc/nginx/templates/default.conf.template"] =
                Path.Combine(baseDir, "web", "nginx", "default.conf.template");
        }

        // Region-keyed rackdc mount; single mode → one "scylla" node in "nam", multi mode → one
        // node per region. Derived from ScyllaKeyspace so the list can't drift.
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

    /// <summary>Rewrites Aspire's blank <c>.env</c> RHSes with concrete secret/parameter/bind-mount
    /// values. Unknown keys are left untouched so future <see cref="InterfoldAppHost.Configure"/>
    /// additions degrade gracefully (blank key surfaces to the operator).</summary>
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

    /// <summary>Reads <paramref name="envPath"/>, applies <paramref name="replacements"/> in
    /// place, and returns (rewrittenCount, unmatchedBlankKeys). Internal so unit tests can drive
    /// the comment-pair logic directly against an in-memory .env.</summary>
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

            // Comment ↔ following KEY= line pair.
            if (line.StartsWith("# Bind mount source for ", StringComparison.Ordinal))
            {
                pendingBindMountKey = line["# Bind mount source for ".Length..].Trim();
                continue;
            }

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
                // Unrecognised blank RHS; operator may need to fix it before `docker compose up`.
                skipped.Add(key);
            }

            pendingBindMountKey = null;
        }

        File.WriteAllLines(envPath, lines);
        return (rewritten, skipped);
    }

    /// <summary>Inserts <c>pull_policy: never</c> after <c>image: "${CASSANDRA_IMAGE}"</c> on
    /// the cassandra service; idempotent. Uses the placeholder token as the anchor (unique to
    /// the cassandra service in Aspire's <c>AddDockerfile("cassandra", ...)</c> emission) so we
    /// never stamp another service. Targeted find+insert avoids a full YAML round-trip that
    /// could reorder Aspire's other keys or reflow compose anchors.</summary>
    internal static void StampCassandraPullPolicyNever(string composePath, PhaseLogger? logger = null)
    {
        var lines = File.ReadAllLines(composePath).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (!line.Contains("${CASSANDRA_IMAGE}", StringComparison.Ordinal)) continue;

            // Idempotency guard so re-runs of `bootstrap publish` don't double-stamp.
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
        // No cassandra service in the compose file — older cassandra-mode setups that skip
        // AddDockerfile don't need the stamp. No-op rather than throw.
        logger?.Warn("compose: no ${CASSANDRA_IMAGE} anchor found; pull_policy stamp skipped");
    }

    private static string SetupAnchor()
    {
        // The bind-mount source files (rackdc, nginx template) are staged under baseDir by
        // Orchestrator.RunAsync via EmbeddedSupportFiles.EnsureExtracted, so we only ensure
        // the anchor directory exists here.
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
        // Aspire 13.x publish requires `--operation publish --step publish`. Without --step
        // the AppHost emits only aspire-manifest.json then blocks on the CLI backchannel
        // (we're not under the CLI). The docker-compose publisher is selected by the
        // AddDockerComposeEnvironment registration in InterfoldAppHost.Configure, not by --publisher.
        var publishArgs = new[]
        {
            "--operation", "publish",
            "--step", "publish",
            "--output-path", options.OutputDir,
        };

        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = publishArgs,
            DisableDashboard = true,
        });

        var (includeScylla, includeCassandra, scyllaTopology) = TranslateDatabaseMode(config.DatabaseMode);

        // Parameters:* injected via IConfiguration; Aspire writes secret parameter values into
        // the .env file rather than the compose YAML at publish time. Seeded from the shared
        // enumerator; extras below are graph-only knobs (topology, image, ports, cluster name)
        // that never round-trip through .env.
        var injected = EnumerateSharedAspireParameters(config, secrets)
            .ToDictionary(p => p.ConfigKey, p => (string?)p.Value, StringComparer.Ordinal);

        // ClusterName is IConfiguration-read (like include-scylla / scylla-topology) rather than
        // an Aspire Parameter — it also feeds Scylla's WithArgs list and that overload takes
        // plain strings. Baked into compose as both CASSANDRA_CLUSTER_NAME and --cluster-name.
        injected[AppHostParameterKeys.ClusterName] = config.ClusterName;
        injected[AppHostParameterKeys.IncludeScylla] = BoolWire.ToWireValue(includeScylla);
        injected[AppHostParameterKeys.IncludeCassandra] = BoolWire.ToWireValue(includeCassandra);
        injected[AppHostParameterKeys.ScyllaTopology] = scyllaTopology.ToWireValue();
        // Bootstrapper always uses a pre-built API image; this switches off the AddProject<> path.
        injected[AppHostParameterKeys.ApiImage] = config.ApiImage;
        // Aspire dev dashboard would pull an MCR-nightly image at compose-up — unwanted in prod.
        injected[AppHostParameterKeys.IncludeDashboard] = BoolWire.FalseValue;
        // WebHttps implies IncludeWeb (cert wiring is useless without the container). WebTls stays
        // driven by webHttps alone so includeWeb-only stacks get HTTP-only for external-proxy setups.
        injected[AppHostParameterKeys.IncludeWeb] = BoolWire.ToWireValue(config.Deployment.IncludeWeb || config.Deployment.WebHttps);
        injected[AppHostParameterKeys.WebTls] = BoolWire.ToWireValue(config.Deployment.WebHttps);
        // nginx server_name accepts DNS/IP literals but not CIDR → same "primary host" rule as
        // ConfigPhase.ResolveDerivedDefaults. The `_` fallback in PickServerName covers only the
        // bypass-validation dev path that skips ConfigPhase entirely.
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

    /// <summary>First parseable, non-CIDR entry wins; falls back to nginx's <c>_</c> catch-all
    /// for the bypass-validation dev path (<see cref="ConfigPhase.Validate"/> otherwise rejects
    /// an all-CIDR/empty list before this runs).</summary>
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
