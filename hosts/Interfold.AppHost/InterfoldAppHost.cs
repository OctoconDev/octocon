using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Interfold.AppHost.DevSeed;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;
using Microsoft.Extensions.DependencyInjection;
// Aspire.Hosting.ApplicationModel also defines PersistenceMode; alias to disambiguate.
using PersistenceMode = Interfold.Shared.Contracts.PersistenceMode;

namespace Interfold.AppHost;

/// <summary>Interfold distributed-application resource graph. Consumed by the dev
/// <c>Interfold.AppHost</c> executable (`aspire run`) and by the self-hosting
/// <c>Interfold.Bootstrapper</c> which pre-populates <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>
/// with generated secrets before calling <see cref="Configure"/>.</summary>
public static class InterfoldAppHost
{
    // Health-check names must match between AddHealthChecks().AddCheck and WithHealthCheck
    // or the annotation silently never resolves.
    private const string MsgDbHealthCheckName = "msg-db-health";
    private const string ScyllaHealthCheckName = "scylla-health";

    /// <summary>Per-node CQL readiness gate name (<c>{resource}-cql</c>).</summary>
    private static string ScyllaCqlCheckName(string resourceName) => $"{resourceName}-cql";

    private const string PostgresEndpointName = "postgres";
    private const string CqlEndpointName = "cql";
    private const string HttpEndpointName = "http";
    private const string HttpsEndpointName = "https";

    /// <summary>Scheme for raw TCP endpoints (Postgres, CQL). HTTP/HTTPS go through the
    /// typed overloads.</summary>
    private const string TcpScheme = "tcp";

    private const string ComposeEnvironmentName = "docker-compose";

    /// <summary>Aspire's default dashboard service name (<c>{environment}-dashboard</c>).</summary>
    private const string DashboardComposeServiceName = ComposeEnvironmentName + "-dashboard";

    // Canonical port-slot defaults (mirrored by appsettings.json's Ports block).
    private const int DefaultPostgresPort = 4200;
    private const int DefaultScyllaPort = 9042;
    private const int DefaultCassandraPort = 9043;
    private const int DefaultApiHttpPort = 5000;
    private const int DefaultApiHttpsPort = 5001;
    private const int DefaultApiContainerHttpPort = 5100;
    private const int DefaultApiContainerHttpsPort = 5101;
    private const int DefaultWebHttpPort = 8080;
    private const int DefaultWebHttpsPort = 8081;

    /// <summary>CQL cluster-name fallback for dev `aspire run`; the bootstrapper always overrides.</summary>
    private const string DefaultClusterName = "InterfoldCluster";

    /// <summary>Registers the full Interfold resource graph. Does not call
    /// <c>Build()</c> or <c>Run()</c>.</summary>
    public static void Configure(IDistributedApplicationBuilder builder)
    {
        int Port(string key, int fallback) => int.TryParse(builder.Configuration[key], out var p) ? p : fallback;

        // ParamName keeps parameter declaration and IConfiguration override on the same spelling.
        static string ParamName(string key) => AppHostParameterKeys.ToParameterName(key);
        var postgresPort = Port(AppHostParameterKeys.PortsPostgres, DefaultPostgresPort);
        var scyllaPort = Port(AppHostParameterKeys.PortsScylla, DefaultScyllaPort);
        // Cassandra owns its own port so SharedDbFixture can publish both CQL backends
        // side-by-side; legacy Cassandra-only mode falls back to scyllaPort further down.
        var cassandraPort = Port(AppHostParameterKeys.PortsCassandra, DefaultCassandraPort);
        var apiHttpPort = Port(AppHostParameterKeys.PortsApiHttp, DefaultApiHttpPort);
        var apiHttpsPort = Port(AppHostParameterKeys.PortsApiHttps, DefaultApiHttpsPort);
        // Must match the API image's EXPOSE/ARG defaults (5100/5101, see /Dockerfile);
        // rebuilding the image with different ports means updating both Ports:api-container-*
        // entries so targetPort, ASPNETCORE_*_PORTS, and the compose healthcheck URL all align.
        var apiContainerHttpPort = Port(AppHostParameterKeys.PortsApiContainerHttp, DefaultApiContainerHttpPort);
        var apiContainerHttpsPort = Port(AppHostParameterKeys.PortsApiContainerHttps, DefaultApiContainerHttpsPort);
        var webHttpPort = Port(AppHostParameterKeys.PortsWebHttp, DefaultWebHttpPort);
        var webHttpsPort = Port(AppHostParameterKeys.PortsWebHttps, DefaultWebHttpsPort);

        var includeApi = BoolWire.ParseToggle(builder.Configuration[AppHostParameterKeys.IncludeApi], fallback: true);
        var persistentContainers = BoolWire.ParseToggle(builder.Configuration[AppHostParameterKeys.PersistentContainers], fallback: true);

        // Both CQL backends can be on simultaneously (SharedDbFixture uses this to
        // exercise Scylla + Cassandra under one Aspire host). scylla-topology only
        // affects layout when include-scylla=true.
        var includeScylla = BoolWire.ParseToggle(builder.Configuration[AppHostParameterKeys.IncludeScylla], fallback: true);
        var includeCassandra = BoolWire.ParseToggle(builder.Configuration[AppHostParameterKeys.IncludeCassandra], fallback: false);
        var scyllaTopology = ScyllaTopologyExtensions.Parse(builder.Configuration[AppHostParameterKeys.ScyllaTopology]);
        var isMultiScylla = scyllaTopology == ScyllaTopology.Multi;

        // Resolve regions ONCE so health-check registrations and node resources can't diverge.
        // Multi: one node per ScyllaKeyspace. Single: one NAM node under the un-suffixed name.
        ScyllaKeyspace[] scyllaRegions = isMultiScylla ? Enum.GetValues<ScyllaKeyspace>() : [ScyllaKeyspace.Nam];
        var isMultiScyllaNode = scyllaRegions.Length > 1;

        // CQL cluster identity — Cassandra reads CASSANDRA_CLUSTER_NAME, Scylla takes it
        // via --cluster-name (immune to image bake-time changes). Pure metadata.
        var clusterName = builder.Configuration[AppHostParameterKeys.ClusterName] ?? DefaultClusterName;

        // Off only for MultiNodeScyllaFixture (which shares SharedDbFixture's msg-db
        // instead of spinning up a redundant second Postgres).
        var includePostgres = BoolWire.ParseToggle(builder.Configuration[AppHostParameterKeys.IncludePostgres], fallback: true);

        if (includeApi && !includePostgres)
        {
            throw new InvalidOperationException(
                "Parameters:include-api=true requires Parameters:include-postgres=true (the API depends on msg-db).");
        }

        // Skip host-port TCP probes in test mode where Aspire randomises host ports —
        // WaitFor() then falls back to Running state.
        if (includeApi)
        {
            builder.Services.AddHealthChecks()
                .AddCheck(MsgDbHealthCheckName, HostPortTcpProbe.CreateCheck(postgresPort))
                // scylla-health only matters when Cassandra owns Ports:scylla alone; when
                // include-scylla=true, the per-node {name}-cql checks below take over.
                .AddCheck(ScyllaHealthCheckName, HostPortTcpProbe.CreateCheck(scyllaPort));
        }

        // Per-node CQL readiness gates (always on). Each node's WithHealthCheck below flips
        // WaitFor(previousNode) from "Running" to "Healthy", which serialises Raft joins —
        // Scylla 2026.1's topology coordinator admits one joiner at a time.
        // We poll `docker ps` per-probe (not via a BackgroundService watching
        // ResourceNotificationService) because Aspire.Hosting.Testing's in-process host
        // silently drops hosted services in multi-fixture sessions (multinode-stagger-debug.log).
        if (includeScylla)
        {
            var hcBuilder = builder.Services.AddHealthChecks();
            foreach (var region in scyllaRegions)
            {
                var resourceName = ComposeServices.ToScyllaNodeName(region, multiNode: isMultiScyllaNode);
                hcBuilder.AddAsyncCheck(ScyllaCqlCheckName(resourceName), async ct =>
                    await DockerExecCqlProbe.RunAsync(resourceName, ct).ConfigureAwait(false));
            }
        }

        // Bootstrapper sets include-dashboard=false so self-hosted production stacks
        // don't pull the nightly aspire-dashboard image; dev `aspire run` keeps it.
        var includeDashboard = BoolWire.ParseToggle(builder.Configuration[AppHostParameterKeys.IncludeDashboard], fallback: true);
        builder.AddDockerComposeEnvironment(ComposeEnvironmentName)
            .WithDashboard(includeDashboard)
            .ConfigureComposeFile(compose =>
            {
                compose.AddNetwork(new Network { Name = ComposeNetworks.Scylla, Driver = "bridge" });
                compose.AddNetwork(new Network { Name = ComposeNetworks.Postgres, Driver = "bridge" });
                compose.AddNetwork(new Network { Name = ComposeNetworks.Api, Driver = "bridge" });

                // Dashboard must reach the API for OTLP.
                if (compose.Services.TryGetValue(DashboardComposeServiceName, out var dashboard))
                {
                    dashboard.Networks.Add(ComposeNetworks.Api);
                }
            });

        var postgresUser = builder.AddParameter(ParamName(AppHostParameterKeys.PostgresUser));
        var postgresPassword = builder.AddParameter(ParamName(AppHostParameterKeys.PostgresPassword), secret: true);
        var postgresDb = builder.AddParameter(ParamName(AppHostParameterKeys.PostgresDb), "interfold", publishValueAsDefault: true);
        // Transient init credential. db_init is a disposable cluster-owner that
        // DatabaseInitPhase uses once to mint the real roles, then scrambles. The .env value
        // is intentionally stale by the time the API starts. Default via
        // GenerateParameterDefault so dev `aspire run` and TUnit.Aspire don't need a pre-seed;
        // publish mode overrides via PublishPhase.BuildEnvReplacements.
        var postgresInitPassword = builder.AddParameter(ParamName(AppHostParameterKeys.PostgresInitPassword),
            new GenerateParameterDefault { MinLength = 24 }, secret: true, persist: true);
        var scyllaUser = builder.AddParameter(ParamName(AppHostParameterKeys.ScyllaUser));
        var scyllaPassword = builder.AddParameter(ParamName(AppHostParameterKeys.ScyllaPassword), secret: true);
        // The <user>_admin superuser is minted by DatabaseInitPhase into internal.secrets and
        // is never surfaced here.
        // GenerateParameterDefault covers the dev `aspire run` case (bootstrapper always injects
        // a real PEM in publish mode, config wins over the default). The generated value is a
        // random alphanumeric string — RecoveryCodeResolver.TryLoadEncryptionPrivateKey checks
        // for "BEGIN" and short-circuits, so JWE-encrypted recovery codes silently 400 in dev.
        // That's the accepted trade-off for the no-config F5 target.
        var encryptionPrivateKey = builder.AddParameter(ParamName(AppHostParameterKeys.EncryptionPrivateKey),
            new GenerateParameterDefault { MinLength = 32 }, secret: true, persist: true);

        // Public OAuth client IDs — empty default disables the corresponding provider (see
        // OAuthChallengeServiceCollectionExtensions). Attached to the container path only so
        // the dev project path keeps reading them from launchSettings.json / user-secrets.
        var googleOAuthClientId = builder.AddParameter(ParamName(AppHostParameterKeys.GoogleOAuthClientId), "", publishValueAsDefault: true);
        var discordOAuthClientId = builder.AddParameter(ParamName(AppHostParameterKeys.DiscordOAuthClientId), "", publishValueAsDefault: true);
        var appleOAuthClientId = builder.AddParameter(ParamName(AppHostParameterKeys.AppleOAuthClientId), "", publishValueAsDefault: true);

        // API runtime parameters managed end-to-end by the bootstrapper (ConfigPhase prompts,
        // ConfigureApiSelfHostEnv pipes to the container as OCTOCON_*).
        var scyllaKeyspace = builder.AddParameter(ParamName(AppHostParameterKeys.ScyllaKeyspace), ScyllaKeyspace.Nam.ToWire(), publishValueAsDefault: true);
        var oauthCallbackBaseUrl = builder.AddParameter(ParamName(AppHostParameterKeys.OAuthCallbackBaseUrl), "", publishValueAsDefault: true);
        var jwtAuthority = builder.AddParameter(ParamName(AppHostParameterKeys.JwtAuthority), "", publishValueAsDefault: true);
        var jwtAudience = builder.AddParameter(ParamName(AppHostParameterKeys.JwtAudience), "octocon", publishValueAsDefault: true);
        var corsAllowedOrigins = builder.AddParameter(ParamName(AppHostParameterKeys.CorsAllowedOrigins), "", publishValueAsDefault: true);

        // Operator tuning — every default matches the API's compile-time fallback so a
        // fresh bootstrap reproduces "env var unset" behaviour. Empty for the nullable
        // knobs (avatars, OTLP, socket threshold) — ApplyStorage / ApplyObservability
        // normalise empty → null.
        var nodeGroup = builder.AddParameter(ParamName(AppHostParameterKeys.NodeGroup), "auxiliary", publishValueAsDefault: true);
        var avatarStorageRoot = builder.AddParameter(ParamName(AppHostParameterKeys.AvatarStorageRoot), "", publishValueAsDefault: true);
        var avatarPublicBase = builder.AddParameter(ParamName(AppHostParameterKeys.AvatarPublicBase), "", publishValueAsDefault: true);

        // Resolved at AppHost-build time so the OCTOCON_AVATAR_STORAGE_ROOT env var and the
        // WithVolume mount below share one path. Blank → the image's pre-created /app/data/avatars
        // (app:app ownership baked in via Interfold.Api.Host/data/avatars/.gitkeep so Docker's
        // named-volume init inherits it and the app user, UID 1654, can write without chown).
        // Operators overriding the path opt out of the managed volume.
        var rawAvatarStorageRoot = builder.Configuration[AppHostParameterKeys.AvatarStorageRoot];
        var effectiveAvatarStorageRoot = string.IsNullOrWhiteSpace(rawAvatarStorageRoot)
            ? ContainerMountPaths.InterfoldAvatars
            : rawAvatarStorageRoot;
        var useDefaultAvatarStorageRoot = string.IsNullOrWhiteSpace(rawAvatarStorageRoot);
        var otlpEndpoint = builder.AddParameter(ParamName(AppHostParameterKeys.OtlpEndpoint), "", publishValueAsDefault: true);
        var socketBatchBytesThreshold = builder.AddParameter(ParamName(AppHostParameterKeys.SocketBatchBytesThreshold), "", publishValueAsDefault: true);
        var dbRetryAttempts = builder.AddParameter(ParamName(AppHostParameterKeys.DbRetryAttempts), "3", publishValueAsDefault: true);
        var dbRetryInitialDelayMs = builder.AddParameter(ParamName(AppHostParameterKeys.DbRetryInitialDelayMs), "100", publishValueAsDefault: true);
        var dbRetryMaxDelayMs = builder.AddParameter(ParamName(AppHostParameterKeys.DbRetryMaxDelayMs), "1500", publishValueAsDefault: true);
        var hydrationMaxConcurrency = builder.AddParameter(ParamName(AppHostParameterKeys.HydrationMaxConcurrency), "8", publishValueAsDefault: true);

        // Reject well-known default passwords in dev mode. Each check is gated on the
        // matching include-* toggle so opt-out fixtures don't need placeholder creds. Publish
        // mode relies on the operator's shell guard.
        if (!builder.ExecutionContext.IsPublishMode)
        {
            if (includeScylla)
            {
                var scyllaPasswordValue = builder.Configuration[AppHostParameterKeys.ScyllaPassword];
                if (string.IsNullOrEmpty(scyllaPasswordValue) || scyllaPasswordValue == "cassandra")
                {
                    throw new InvalidOperationException(
                        "SCYLLA_PASSWORD must be set to a non-default value. " +
                        "The well-known default 'cassandra' is not allowed.\n" +
                        "Fix: dotnet user-secrets set \"Parameters:scylla-password\" \"<your-secure-password>\" " +
                        "--project hosts/Interfold.AppHost");
                }

                var scyllaUserValue = builder.Configuration[AppHostParameterKeys.ScyllaUser];
                if (string.IsNullOrEmpty(scyllaUserValue) || scyllaUserValue == "cassandra")
                {
                    throw new InvalidOperationException(
                        "SCYLLA_USER must be set to a non-default value. " +
                        "The well-known default 'cassandra' is not allowed.\n" +
                        "Fix: dotnet user-secrets set \"Parameters:scylla-user\" \"<your-username>\" " +
                        "--project hosts/Interfold.AppHost");
                }
            }

            if (includePostgres)
            {
                var postgresPasswordValue = builder.Configuration[AppHostParameterKeys.PostgresPassword];
                if (string.IsNullOrEmpty(postgresPasswordValue) || postgresPasswordValue == "postgres")
                {
                    throw new InvalidOperationException(
                        "POSTGRES_PASSWORD must be set to a non-default value. " +
                        "The well-known default 'postgres' is not allowed.\n" +
                        "Fix: dotnet user-secrets set \"Parameters:postgres-password\" \"<your-secure-password>\" " +
                        "--project hosts/Interfold.AppHost");
                }

                var postgresUserValue = builder.Configuration[AppHostParameterKeys.PostgresUser];
                if (string.IsNullOrEmpty(postgresUserValue) || postgresUserValue == "postgres")
                {
                    throw new InvalidOperationException(
                        "POSTGRES_USER must be set to a non-default value. " +
                        "The well-known default 'postgres' is not allowed.\n" +
                        "Fix: dotnet user-secrets set \"Parameters:postgres-user\" \"<your-username>\" " +
                        "--project hosts/Interfold.AppHost");
                }
            }
        }

        // Postgres (TimescaleDB). Skipped when include-postgres=false. First-boot pattern:
        // POSTGRES_USER=db_init (disposable bootstrap superuser — can't demote OID 10 per
        // postgres' commands/user.c::AlterRole), POSTGRES_PASSWORD is the transient init
        // credential DatabaseInitPhase scrambles after seeding, no POSTGRES_DB (the app DB is
        // created owned by the admin role, not db_init, for consistent DDL gating).
        IResourceBuilder<ContainerResource>? msgDb = null;
        if (includePostgres)
        {
            msgDb = builder.AddContainer(ComposeServices.Postgres, "timescale/timescaledb", "latest-pg18")
                .WithContainerNetworkAlias(ComposeServices.Postgres)
                .WithEnvironment(ContainerEnvNames.PostgresUser, PostgresRoles.Init)
                .WithEnvironment(ContainerEnvNames.PostgresPassword, postgresInitPassword)
                .WithEnvironment(ContainerEnvNames.PgData, ContainerMountPaths.PostgresPgData)
                // Force scram-sha-256 for every host entry — the initdb default appends a
                // `host all all 127.0.0.1/32 trust` line first and pg_hba.conf is first-match-wins,
                // so without this loopback TCP connections silently bypass the db_init password
                // scramble. `local all all trust` still applies to Unix sockets, which is how
                // DatabaseInitPhase mints the app + admin roles pre-scramble.
                .WithEnvironment(ContainerEnvNames.PostgresInitDbArgs, "--auth-host=scram-sha-256")
                // pg_ctl's 60s default isn't enough on slow DinD disks — the timescaledb-tune
                // init script triggers a fast-shutdown checkpoint before postgres re-execs in
                // normal mode, and a slow checkpoint kills the container before WaitForPostgresAsync
                // sees normal mode. 300s buys headroom.
                .WithEnvironment(ContainerEnvNames.PgCtlTimeout, "300")
                // Bypass timescaledb-tune's cgroup-v2 memory autodetection (missing
                // /sys/fs/cgroup/memory.max on some Docker Desktop / restricted hosts panics
                // the init script). Conservative defaults; operators can override via
                // docker-compose.override.yaml.
                .WithEnvironment(ContainerEnvNames.TsTuneMemory, "1GB")
                .WithEnvironment(ContainerEnvNames.TsTuneNumCpus, "2")
                .WithEndpoint(port: postgresPort, targetPort: 5432, name: PostgresEndpointName, scheme: TcpScheme)
                .PublishAsDockerComposeService((_, service) =>
                {
                    service.Networks = [ComposeNetworks.Postgres];
                    service.Healthcheck = ComposeHealthcheck.CmdShell(
                        $"pg_isready -U ${ContainerEnvNames.PostgresUser} -d postgres",
                        interval: "10s", timeout: "5s", retries: 10, startPeriod: "15s");
                });
            if (includeApi)
                msgDb.WithHealthCheck(MsgDbHealthCheckName);
            if (persistentContainers)
                msgDb.AsPersistent(ComposeVolumes.PostgresData, ContainerMountPaths.PostgresData);
        }

        // API waits on each included CQL backend before starting.
        var cqlEndpointOwners = new List<IResourceBuilder<ContainerResource>>();

        if (includeScylla)
        {
            IResourceBuilder<ContainerResource>? previousNode = null;

            foreach (var region in scyllaRegions)
            {
                var regionWire = region.ToWire();
                var name = ComposeServices.ToScyllaNodeName(region, multiNode: isMultiScyllaNode);
                var seeds = previousNode is null
                    ? $"--seeds={name}"
                    : $"--seeds={ComposeServices.ToScyllaNodeName(scyllaRegions[0], multiNode: true)},{ComposeServices.ToScyllaNodeName(scyllaRegions[1], multiNode: true)}";

                var nodeArgs = new List<string>
                {
                    seeds, "--smp", "1", "--memory", "750M", "--overprovisioned", "1",
                    "--developer-mode", "1", "--authenticator", "PasswordAuthenticator",
                    "--authorizer", "CassandraAuthorizer",
                    "--endpoint-snitch", "GossipingPropertyFileSnitch",
                    "--api-address", "0.0.0.0", "--broadcast-address", name,
                    "--cluster-name", clusterName,
                };
                if (isMultiScylla)
                {
                    // Ring-delay 60s (vs 30s default) — extra settle time for the Raft
                    // topology coordinator between multi-DC joiners.
                    nodeArgs.Add("--ring-delay-ms");
                    nodeArgs.Add("60000");
                }

                var node = builder.AddContainer(name, "scylladb/scylla", "2026.1")
                    .WithContainerNetworkAlias(name)
                    .WithArgs([.. nodeArgs])
                    .WithBindMount($"../../db/scylla/cassandra-rackdc.{regionWire}.properties", "/etc/scylla/cassandra-rackdc.properties", isReadOnly: true)
                    .WithEnvironment(ContainerEnvNames.CqlshUser, scyllaUser)
                    .WithEnvironment(ContainerEnvNames.CqlshPassword, scyllaPassword)
                    .PublishAsDockerComposeService((_, service) =>
                    {
                        service.Networks = [ComposeNetworks.Scylla];
                        // Probe the actual CQL endpoint (not `nodetool status`) so
                        // depends_on:service_healthy really means "CQL reachable as the app user"
                        // — on slow DinD hosts the CQL listener can lag NORMAL mode by 60–90s.
                        // `$$VAR` is deliberate: docker-compose interpolates ${VAR} at parse time
                        // against the host shell, `$$VAR` escapes to a literal that the
                        // container's /bin/sh expands against the container env.
                        service.Healthcheck = ComposeHealthcheck.CmdShell(
                            $"cqlsh -u \"$${ContainerEnvNames.CqlshUser}\" -p \"$${ContainerEnvNames.CqlshPassword}\" -e 'DESCRIBE CLUSTER' >/dev/null 2>&1",
                            interval: "15s", timeout: "10s", retries: 20, startPeriod: "30s");
                    });

                if (persistentContainers)
                    node.AsPersistent(
                        isMultiScyllaNode ? ComposeVolumes.ScyllaRegionData(regionWire) : ComposeVolumes.ScyllaData,
                        ContainerMountPaths.ScyllaData);

                // HealthCheckAnnotation flips WaitFor(previousNode) from "Running" to
                // "Healthy" — serialises multi-DC joins so Raft doesn't ban concurrent joiners.
                node.WithHealthCheck(ScyllaCqlCheckName(name));

                if (previousNode is null)
                {
                    node.WithEndpoint(port: scyllaPort, targetPort: 9042, name: CqlEndpointName, scheme: TcpScheme);
                    cqlEndpointOwners.Add(node);
                }
                else
                {
                    node.WaitFor(previousNode);
                }

                previousNode = node;
            }
        }

        if (includeCassandra)
        {
            // Built from db/cassandra/Dockerfile (FROM cassandra:5). The image bakes the
            // required cassandra.yaml overrides (materialized_views_enabled + Password
            // Authenticator/Authorizer) so we skip the runtime wrapper + chown that used to
            // break on GHA. Endpoint port: standalone Cassandra owns Ports:scylla (legacy),
            // co-hosted with Scylla it takes Ports:cassandra so the CQL listeners don't collide.
            var cassandraEndpointPort = includeScylla ? cassandraPort : scyllaPort;

            var cassandra = builder.AddDockerfile(ComposeServices.Cassandra, "../../db/cassandra")
                .WithContainerNetworkAlias(ComposeServices.Cassandra)
                .WithEnvironment(ContainerEnvNames.CassandraClusterName, clusterName)
                .WithEnvironment(ContainerEnvNames.CassandraListenAddress, ComposeServices.Cassandra)
                .WithEnvironment(ContainerEnvNames.CassandraBroadcastAddress, ComposeServices.Cassandra)
                .WithEnvironment(ContainerEnvNames.CassandraBroadcastRpcAddress, ComposeServices.Cassandra)
                .WithEnvironment(ContainerEnvNames.CassandraRpcAddress, "0.0.0.0")
                .WithEnvironment(ContainerEnvNames.CassandraEndpointSnitch, "GossipingPropertyFileSnitch")
                .WithEnvironment(ContainerEnvNames.CassandraNumTokens, "16")
                .WithEnvironment(ContainerEnvNames.CassandraDc, ScyllaKeyspace.Nam.ToWire())
                .WithEnvironment(ContainerEnvNames.CassandraRack, "rack1")
                .WithEnvironment(ContainerEnvNames.MaxHeapSize, "512M")
                .WithEnvironment(ContainerEnvNames.HeapNewSize, "256M")
                .WithEnvironment(ContainerEnvNames.CqlshUser, scyllaUser)
                .WithEnvironment(ContainerEnvNames.CqlshPassword, scyllaPassword)
                .WithEndpoint(port: cassandraEndpointPort, targetPort: 9042, name: CqlEndpointName, scheme: TcpScheme)
                .PublishAsDockerComposeService((_, service) =>
                {
                    service.Networks = [ComposeNetworks.Scylla];
                    service.Healthcheck = ComposeHealthcheck.CmdShell(
                        $"cqlsh -u \"$${ContainerEnvNames.CqlshUser}\" -p \"$${ContainerEnvNames.CqlshPassword}\" -e 'describe cluster' || nodetool status | grep -q '^UN'",
                        interval: "15s", timeout: "10s", retries: 20, startPeriod: "30s");
                });

            if (includeApi && !includeScylla)
            {
                // Attach scylla-health only when Cassandra owns Ports:scylla.
                cassandra.WithHealthCheck(ScyllaHealthCheckName);
            }
            if (persistentContainers)
                cassandra.AsPersistent(ComposeVolumes.CassandraData, ContainerMountPaths.CassandraData);

            cqlEndpointOwners.Add(cassandra);
        }

        // Seed ownership by mode:
        //  - Publish: DatabaseInitPhase in the bootstrapper (docker compose exec).
        //  - Tests (include-api=false): SharedDbFixture drives DbInitHelper directly.
        //  - RunMode dev (include-api=true): DevSeedHostedService, wired below.
        // First cqlEndpointOwners entry is scylla node 0 in the default flow, cassandra when
        // the user picked the cassandra launch profile; mixed scylla+cassandra is test-only
        // so we only ever seed one backend.
        IResourceBuilder<DevSeedResource>? devSeedResource = null;
        if (!builder.ExecutionContext.IsPublishMode && includeApi && includePostgres)
        {
            var firstCqlBackend = cqlEndpointOwners.Count > 0 ? cqlEndpointOwners[0] : null;
            // Aspire persists GenerateParameterDefault outputs to user-secrets, but only
            // after Build() completes — on first run IConfiguration doesn't yet see them.
            // Threading the ParameterResource lets the hosted service read the effective
            // value via GetValueAsync in both first-run and subsequent-run cases.
            devSeedResource = builder.AddDevSeedPipeline(
                msgDb!, firstCqlBackend,
                postgresInitPassword, postgresPassword, scyllaPassword);
        }

        // Interfold API: pre-built image for self-hosting (Parameters:api-image), csproj build
        // for dev (`aspire run`).
        if (includeApi)
        {
            // Guaranteed non-null by the includeApi && !includePostgres guard above.
            var msgDbResource = msgDb!;
            var pgEndpoint = msgDbResource.GetEndpoint(PostgresEndpointName);

            void ConfigureApiCommon(IResourceBuilder<IResourceWithEnvironment> api)
            {
                api.WithEnvironment(OctoconEnvKeys.Persistence, PersistenceMode.ScyllaPostgres.ToWire())
                   .WithEnvironment(OctoconEnvKeys.SingleScyllaInstance, BoolWire.ToWireValue(!isMultiScylla))
                   .WithEnvironment(OctoconEnvKeys.PostgresConnection,
                       ReferenceExpression.Create($"Host={pgEndpoint.Property(EndpointProperty.Host)};Port={pgEndpoint.Property(EndpointProperty.Port)};Database={postgresDb};Username={postgresUser};Password={postgresPassword}"))
                   .WithEnvironment(ContainerEnvNames.EncryptionPrivateKey, encryptionPrivateKey);
            }

            // Dev-project path only. ConfigureApiSelfHostEnv covers the container path via the
            // operator-provided Parameters:jwt-authority etc.; the dev path has no operator so
            // we stamp localhost-derived defaults driven by the AppHost's own port allocation.
            // Empty CORS falls back to "any origin" in the API — noisy in browser devtools but
            // functional; we still narrow it here so socket + fetch calls behave the same as prod.
            void ConfigureApiDevEnv(IResourceBuilder<IResourceWithEnvironment> api)
            {
                var apiHttpsUrl = $"https://localhost:{apiHttpsPort}";
                var webOrigins =
                    $"https://localhost:{webHttpsPort},http://localhost:{webHttpPort}";
                api.WithEnvironment(OctoconEnvKeys.JwtAuthority, apiHttpsUrl)
                   .WithEnvironment(OctoconEnvKeys.JwtAudience, "octocon")
                   .WithEnvironment(OctoconEnvKeys.AuthCallbackBaseUrl, apiHttpsUrl)
                   .WithEnvironment(OctoconEnvKeys.CorsAllowedOrigins, webOrigins)
                   .WithEnvironment(OctoconEnvKeys.ScyllaKeyspace, ScyllaKeyspace.Nam.ToWire());
            }

            // Self-hosting only. Bind-mounts /certs (root CA + leaf PFX from CertificatePhase)
            // read-only; everything else (JWT keys, pepper, OAuth secrets, leaf PFX password)
            // lives in internal.secrets and is loaded by SecretsBootstrapService / Program.cs.
            // Captures apiContainerHttpPort/apiContainerHttpsPort so it can't be static.
            void ConfigureApiSelfHostEnv(IResourceBuilder<ContainerResource> api)
            {
                api.WithBindMount(CertsPaths.HostDir, CertsPaths.ContainerDir, isReadOnly: true)
                   // Override the SDK's HTTP 8080 default so ASPNETCORE_*_PORTS matches
                   // targetPort — HTTPS terminates inside the container using the leaf PFX.
                   .WithEnvironment(ContainerEnvNames.AspNetCoreHttpPorts, apiContainerHttpPort.ToString())
                   .WithEnvironment(ContainerEnvNames.AspNetCoreHttpsPorts, apiContainerHttpsPort.ToString())
                   // Kestrel default-endpoint cert (docs: aspnetcore/fundamentals/servers/kestrel/endpoints#configure-https).
                   // Password moved to internal.secrets:certs:leaf_pfx_password (loaded by Program.cs).
                   .WithEnvironment(ContainerEnvNames.AspNetCoreKestrelDefaultCertPath, CertsPaths.LeafPfx)
                   // TrustController serves /.well-known/interfold-root-ca.{crt,pem,sha256}
                   // from these paths (inside the /certs bind mount). Blank → 404. The sha256
                   // file also drives the HTTP ETag so --rotate-certs invalidates caches.
                   .WithEnvironment(OctoconEnvKeys.TrustRootCaPath, CertsPaths.RootCaCrt)
                   .WithEnvironment(OctoconEnvKeys.TrustRootCaFingerprintPath, CertsPaths.RootCaFingerprint)
                   // Container path only — dev project path still reads launchSettings/user-secrets.
                   // Empty per-provider ID disables that provider (see OAuthChallenge extensions).
                   .WithEnvironment(OctoconEnvKeys.GoogleOAuthClientId, googleOAuthClientId)
                   .WithEnvironment(OctoconEnvKeys.DiscordOAuthClientId, discordOAuthClientId)
                   .WithEnvironment(OctoconEnvKeys.AppleOAuthClientId, appleOAuthClientId)
                   // API runtime config from BootstrapConfig (ConfigPhase). See
                   // docs/configuration.md gotcha #1 — OCTOCON_SCYLLA_KEYSPACE default 'nam'
                   // is wrong for any non-NAM regional stack. Empty CORS falls back to
                   // "any origin" in Program.cs (production foot-gun; bootstrapper derives
                   // a default from Hosts).
                   .WithEnvironment(OctoconEnvKeys.ScyllaKeyspace, scyllaKeyspace)
                   .WithEnvironment(OctoconEnvKeys.AuthCallbackBaseUrl, oauthCallbackBaseUrl)
                   .WithEnvironment(OctoconEnvKeys.JwtAuthority, jwtAuthority)
                   .WithEnvironment(OctoconEnvKeys.JwtAudience, jwtAudience)
                   .WithEnvironment(OctoconEnvKeys.CorsAllowedOrigins, corsAllowedOrigins)
                   // Operator tuning — every default matches the API's compile-time fallback.
                   // Empty for the nullable knobs (avatars, OTLP, socket threshold);
                   // ApplyStorage / ApplyObservability normalise empty → null.
                   .WithEnvironment(OctoconEnvKeys.NodeGroup, nodeGroup)
                   // OCTOCON_AVATAR_STORAGE_ROOT carries the RESOLVED literal path so the env
                   // var and the WithVolume mount below share it.
                   .WithEnvironment(OctoconEnvKeys.AvatarStorageRoot, effectiveAvatarStorageRoot)
                   .WithEnvironment(OctoconEnvKeys.AvatarPublicBase, avatarPublicBase)
                   .WithEnvironment(OctoconEnvKeys.OtlpEndpoint, otlpEndpoint)
                   .WithEnvironment(OctoconEnvKeys.SocketBatchBytesThreshold, socketBatchBytesThreshold)
                   .WithEnvironment(OctoconEnvKeys.DbRetryAttempts, dbRetryAttempts)
                   .WithEnvironment(OctoconEnvKeys.DbRetryInitialDelayMs, dbRetryInitialDelayMs)
                   .WithEnvironment(OctoconEnvKeys.DbRetryMaxDelayMs, dbRetryMaxDelayMs)
                   .WithEnvironment(OctoconEnvKeys.HydrationMaxConcurrency, hydrationMaxConcurrency);

                // Managed avatar volume only when persistent AND path is the image's default —
                // Docker's volume-init inherits app:app ownership from the pre-created dir
                // (Interfold.Api.Host/data/avatars/.gitkeep) so the app user (UID 1654) can
                // write without chown. Operator-supplied paths opt out (no matching in-image
                // dir means EACCES); they own their own bind mount + permissions.
                if (persistentContainers && useDefaultAvatarStorageRoot)
                {
                    api.WithVolume(ComposeVolumes.InterfoldAvatars, ContainerMountPaths.InterfoldAvatars);
                }
            }

            var apiImageRef = builder.Configuration[AppHostParameterKeys.ApiImage];
            if (!string.IsNullOrEmpty(apiImageRef))
            {
                var apiImage = ImageRef.Parse(apiImageRef);
                var apiContainer = builder.AddContainer(ComposeServices.InterfoldApi, apiImage.Image, apiImage.Tag)
                    .WithHttpEndpoint(port: apiHttpPort, targetPort: apiContainerHttpPort, name: HttpEndpointName)
                    .WithHttpsEndpoint(port: apiHttpsPort, targetPort: apiContainerHttpsPort, name: HttpsEndpointName)
                    .WithHttpHealthCheck(HealthEndpoints.Ready, endpointName: HttpEndpointName)
                    .WithExternalHttpEndpoints()
                    .WaitFor(msgDbResource)
                    .PublishAsDockerComposeService(ApiComposeServicePublisher(apiContainerHttpPort));
                ConfigureApiCommon(apiContainer);
                ConfigureApiSelfHostEnv(apiContainer);
                foreach (var owner in cqlEndpointOwners)
                    apiContainer.WaitFor(owner);
                // Symmetry with the AddProject branch — bootstrapper's publish never sees
                // devSeedResource so this is a no-op in publish mode.
                if (devSeedResource is not null)
                    apiContainer.WaitFor(devSeedResource);
            }
            else
            {
                var apiProject = builder.AddProject<Projects.Interfold_Api_Host>(ComposeServices.InterfoldApi)
                    .WithHttpEndpoint(port: apiHttpPort, targetPort: apiContainerHttpPort, name: HttpEndpointName)
                    .WithHttpsEndpoint(port: apiHttpsPort, targetPort: apiContainerHttpsPort, name: HttpsEndpointName)
                    .WithHttpHealthCheck(HealthEndpoints.Ready, endpointName: HttpEndpointName)
                    .WithExternalHttpEndpoints()
                    .WaitFor(msgDbResource)
                    .PublishAsDockerComposeService(ApiComposeServicePublisher(apiContainerHttpPort));
                ConfigureApiCommon(apiProject);
                ConfigureApiDevEnv(apiProject);
                foreach (var owner in cqlEndpointOwners)
                    apiProject.WaitFor(owner);
                if (devSeedResource is not null)
                    apiProject.WaitFor(devSeedResource);
            }
        }

        var includeWeb = BoolWire.ParseToggle(builder.Configuration[AppHostParameterKeys.IncludeWeb], fallback: true);
        // Opt-in HTTPS termination via nginx's envsubst-on-templates entrypoint — bootstrapper
        // bind-mounts the leaf cert/key + a generated template. Dev leaves this off.
        var webTls = BoolWire.ParseToggle(builder.Configuration[AppHostParameterKeys.WebTls], fallback: false);
        if (includeWeb)
        {
            var web = builder.AddContainer(ComposeServices.OctoconWeb, "ghcr.io/azyyyyyy/octocon-wasm", "latest")
                .WithContainerNetworkAlias(ComposeServices.OctoconWeb);

            if (webTls)
            {
                // PublishPhase feeds server_name in via config (picked from Deployment.Hosts).
                // `_` is the nginx catch-all for dev callers who flip the toggle without it.
                var serverName = builder.Configuration[AppHostParameterKeys.WebServerName];
                if (string.IsNullOrWhiteSpace(serverName)) serverName = "_";

                web = web
                    .WithHttpEndpoint(port: webHttpPort, targetPort: 80, name: HttpEndpointName)
                    .WithHttpsEndpoint(port: webHttpsPort, targetPort: 443, name: HttpsEndpointName)
                    .WithBindMount(CertsPaths.HostDir, CertsPaths.ContainerDir, isReadOnly: true)
                    .WithBindMount(
                        "../../web/nginx/default.conf.template",
                        "/etc/nginx/templates/default.conf.template",
                        isReadOnly: true)
                    .WithEnvironment(ContainerEnvNames.NginxServerName, serverName)
                    .WithEnvironment(ContainerEnvNames.NginxSslCertFile, CertsPaths.LeafCrt)
                    .WithEnvironment(ContainerEnvNames.NginxSslKeyFile, CertsPaths.LeafKey)
                    // Precomputed `:<port>` (or empty for 443) so the :80 → :443 redirect
                    // lands on the bound host port (webHttpsPort, default 8081) instead of
                    // an unbound 443 the browser would infer.
                    .WithEnvironment(
                        ContainerEnvNames.NginxHttpsPortSuffix,
                        webHttpsPort == 443 ? string.Empty : $":{webHttpsPort}")
                    // Restrict envsubst to NGINX_* so upstream $host/$uri stay untouched.
                    .WithEnvironment(ContainerEnvNames.NginxEnvsubstFilter, "^NGINX_")
                    .WithHttpHealthCheck("/", endpointName: HttpEndpointName)
                    // Must be AFTER the endpoint registrations — Aspire's external-endpoint
                    // pass walks the current endpoint set, so calling this earlier produces
                    // `expose:` with no `ports:` in the emitted compose.
                    .WithExternalHttpEndpoints()
                    .PublishAsDockerComposeService((_, service) =>
                    {
                        service.Networks = [ComposeNetworks.Api];
                        // -k: leaf is signed by the private root CA the container doesn't trust.
                        service.Healthcheck = ComposeHealthcheck.Cmd(
                            interval: "15s", timeout: "5s", retries: 5, startPeriod: "10s",
                            command: ["curl", "-kf", "https://localhost:443/"]);
                    });
            }
            else
            {
                // Upstream octocon-wasm image only listens on :8080 (Dockerfile.wasm bakes it
                // into /etc/nginx/conf.d/default.conf). Publishing web-https here would shadow
                // web-http with a second port serving plaintext, so Ports:web-https is unused
                // in this branch.
                web = web
                    .WithHttpEndpoint(port: webHttpPort, targetPort: 8080, name: HttpEndpointName)
                    .WithHttpHealthCheck("/", endpointName: HttpEndpointName)
                    .WithExternalHttpEndpoints()
                    .PublishAsDockerComposeService((_, service) =>
                    {
                        service.Networks = [ComposeNetworks.Api];
                        service.Healthcheck = ComposeHealthcheck.Cmd(
                            interval: "15s", timeout: "5s", retries: 5, startPeriod: "10s",
                            command: ["curl", "-f", "http://localhost:8080/"]);
                    });
            }
            _ = web;
        }
    }

    private static void PromoteDependenciesToHealthy(Service service, params string[] dependencyNames)
    {
        foreach (var dep in dependencyNames)
        {
            if (service.DependsOn.TryGetValue(dep, out var composeDep))
                composeDep.Condition = ComposeDependencyCondition.ServiceHealthy;
        }
    }

    private static Action<Aspire.Hosting.Docker.DockerComposeServiceResource, Aspire.Hosting.Docker.Resources.ComposeNodes.Service> ApiComposeServicePublisher(int apiContainerHttpPort)
    {
        return (_, service) =>
        {
            service.Networks = [ComposeNetworks.Scylla, ComposeNetworks.Postgres, ComposeNetworks.Api];
            service.Healthcheck = ComposeHealthcheck.Cmd(
                interval: "15s", timeout: "5s", retries: 10, startPeriod: "20s",
                command: ["curl", "-f", $"http://localhost:{apiContainerHttpPort}{HealthEndpoints.Ready}"]);
            // Gate on msg-db + scylla being CQL-ready (not just Running). Without this the
            // bootstrapper's `up` command races the API into scylla before gossip-bootstrap
            // finishes and ScyllaMigrationService.StartingAsync crashes with
            // "connection refused on 9042".
            PromoteDependenciesToHealthy(service, ComposeServices.Postgres, ComposeServices.ScyllaSingle, ComposeServices.Cassandra);
        };
    }
}
