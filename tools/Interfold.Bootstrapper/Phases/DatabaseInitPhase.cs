using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Shared.Contracts.Configuration;
using Interfold.DatabaseBootstrap;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// Phase 5.5 — owns the database admin work that previously lived in the
/// <c>pg-bootstrap-auth</c> / <c>scylla-bootstrap-auth</c> init containers. Brings up the
/// stateful services in isolation, then hands off seed orchestration to
/// <see cref="PostgresSeeder"/> / <see cref="ScyllaSeeder"/> via the compose-exec executors
/// in this folder. After this phase returns the cluster is ready for
/// <see cref="LaunchPhase"/> to <c>up -d</c> the rest of the graph.
/// </summary>
/// <remarks>
/// <para>
/// All admin/seed operations are driven via <c>docker compose exec</c> against the running
/// containers — no admin password ever appears in <c>docker-compose.yaml</c>, <c>.env</c>,
/// or AppHost parameters. State is detected from inside the cluster (the <c>&lt;user&gt;_admin</c>
/// role's existence, plus the app user's <c>rolsuper</c> flag), so a rerun against a
/// populated data volume short-circuits cleanly.
/// </para>
/// <para>
/// The actual SQL/CQL bodies live in <c>Interfold.DatabaseBootstrap</c> so the same logic
/// runs against the in-process test fixtures via <c>DbInitHelper</c> + the Npgsql / DataStax
/// driver adapters. This file is intentionally lean: it owns only the docker-compose
/// orchestration, the cold-start wait loops, and the executor wire-up.
/// </para>
/// </remarks>
internal static class DatabaseInitPhase
{
    private static readonly string Phase = BootstrapPhase.DbInit.ToWireName();
    private const string PostgresService = ComposeServices.Postgres;

    public static async Task RunAsync(
        BootstrapOptions options,
        BootstrapConfig config,
        GeneratedSecrets secrets,
        FirebaseSeedInputs firebase,
        PhaseLogger logger,
        CancellationToken ct)
    {
        logger.PhaseStart(Phase);

        var composeFile = PhaseArtifactLoader.RequireComposeFileOrFail(options, logger, Phase);

        // Scylla service name depends on launch profile. The 'cassandra' fallback and
        // multi-region scylla deployments use a different container alias - we drive the
        // first node only because all admin operations propagate via gossip.
        var scyllaService = ResolveScyllaServiceName(config);
        var scyllaPort = ResolveScyllaPort();

        if (CassandraImagePhase.IsCassandraDeployment(config))
        {
            await CassandraImagePhase.EnsureBuiltAsync(logger, ct).ConfigureAwait(false);
        }

        // Bring up only the stateful services first. We deliberately don't start the API or
        // any other services so the API doesn't race against an unconfigured Postgres.
        await Util.DockerCompose.UpCheckedAsync(composeFile, [PostgresService, scyllaService], logger, ct).ConfigureAwait(false);

        var seederLogger = new PhaseLoggerAdapter(logger);
        var pgExecutor = new ComposeExecPostgresExecutor(composeFile, PostgresService, seederLogger);
        var scExecutor = new ComposeExecScyllaExecutor(composeFile, scyllaService, seederLogger);
        var pgOptions = BuildPostgresSeedOptions(config, secrets, firebase, scyllaService, scyllaPort);
        var scOptions = new ScyllaSeedOptions(
            AppUser: secrets.ScyllaUser,
            AppPassword: secrets.ScyllaPassword,
            AdminUser: $"{secrets.ScyllaUser}_admin",
            AdminPassword: secrets.ScyllaAdminPassword,
            LockDefaultCassandra: true);

        await WaitForPostgresAsync(composeFile, logger, ct).ConfigureAwait(false);
        await PostgresSeeder.BootstrapAsync(pgExecutor, pgOptions, seederLogger, ct).ConfigureAwait(false);

        // Hidden testability hook: halt the phase between Postgres and Scylla so
        // DbInitFaultRecoveryTests can confirm a rerun resumes cleanly. We throw rather than
        // return so the Orchestrator's try/catch surfaces a non-zero exit and skips the
        // Launch phase (which would otherwise try to `compose up` an un-initialised stack).
        if (string.Equals(options.FaultInject, BootstrapPhase.DbPostgres.ToFaultInjectToken(), StringComparison.OrdinalIgnoreCase))
        {
            logger.Warn("--fault-inject=after-db-postgres triggered; halting before scylla init.");
            throw new InvalidOperationException("fault-inject:after-db-postgres");
        }

        await WaitForScyllaAsync(composeFile, scyllaService, scExecutor, scOptions, logger, ct).ConfigureAwait(false);
        await ScyllaSeeder.BootstrapAsync(scExecutor, scOptions, seederLogger, ct).ConfigureAwait(false);

        logger.PhaseDone(Phase);
    }

    private static PostgresSeedOptions BuildPostgresSeedOptions(
        BootstrapConfig config,
        GeneratedSecrets secrets,
        FirebaseSeedInputs firebase,
        string scyllaService,
        int scyllaPort)
    {
        return new PostgresSeedOptions(
            InitUser: PostgresRoles.Init,
            InitPassword: secrets.PostgresInitPassword,
            AppUser: secrets.PostgresUser,
            AppPassword: secrets.PostgresPassword,
            AdminUser: $"{secrets.PostgresUser}_admin",
            AdminPassword: secrets.PostgresAdminPassword,
            DefaultDatabase: config.PostgresDatabase,
            GoogleOAuthClientSecret: config.OAuth.GoogleClientSecret ?? string.Empty,
            DiscordOAuthClientSecret: config.OAuth.DiscordClientSecret ?? string.Empty,
            AppleOAuthClientSecret: config.OAuth.AppleClientSecret ?? string.Empty,
            EncryptionPepper: secrets.EncryptionPepper,
            // Self-hosted deployments reach scylla via the docker network using its container
            // alias. The matching DataStax driver inside the API resolves it inside the same
            // network so the alias is sufficient.
            ScyllaContactPoints: scyllaService,
            ScyllaLocalDatacenter: "nam",
            ScyllaAppUser: secrets.ScyllaUser,
            ScyllaAppPassword: secrets.ScyllaPassword,
            ScyllaPort: scyllaPort,
            ScyllaAdminUser: $"{secrets.ScyllaUser}_admin",
            ScyllaAdminPassword: secrets.ScyllaAdminPassword,
            JwtRsa256PrivateKeyPem: secrets.JwtRsa256PrivateKeyPem,
            JwtEs256PrivateKeyPem: secrets.JwtEs256PrivateKeyPem,
            DeepLinkSecret: secrets.DeepLinkSecret,
            LeafPfxPassword: secrets.LeafPfxPassword,
            // Production callers always finish by scrambling the init credential in-cluster
            // so the .env value sitting in the operator's deployment dir is intentionally
            // stale by the time the API starts.
            ScrambleInitUserPassword: true,
            FirebaseAndroidClientJson: firebase.AndroidClientJson,
            FirebaseIosClientJson: firebase.IosClientJson,
            FirebaseWebClientJson: firebase.WebClientJson,
            FcmServiceAccountJson: firebase.ServiceAccountJson);
    }

    private static string ResolveScyllaServiceName(BootstrapConfig config)
        => BackupPhase.ResolveScyllaSeed(config).Service;

    private static int ResolveScyllaPort()
    {
        // CQL port the API uses to reach Scylla *over the compose docker network* (resolving the
        // scylla service name via docker DNS). That target is always the container's listening
        // port — 9042 — irrespective of whatever host port the operator (or test fixture) chose
        // for external access via `config.ports.scylla`. The host-port choice flows into the
        // compose YAML via PublishPhase; here we deliberately stay on the in-network port.
        return 9042;
    }

    // -------- Bring-up --------

    // -------- Wait loops (transport-specific) --------

    private static Task WaitForPostgresAsync(string composeFile, PhaseLogger logger, CancellationToken ct)
    {
        return Util.PostgresReadinessProbe.WaitAsync(
            composeFile,
            PostgresService,
            new Interfold.DatabaseBootstrap.PostgresReadinessOptions(TimeSpan.FromMinutes(10), 3, PostgresRoles.Init),
            logger,
            ct);
    }

    private static Task WaitForScyllaAsync(
        string composeFile, string scyllaService,
        IScyllaExecutor executor, ScyllaSeedOptions options,
        PhaseLogger logger, CancellationToken ct)
    {
        _ = composeFile;
        _ = scyllaService;
        return Util.ScyllaReadinessProbe.WaitAsync(
            executor,
            options,
            new Interfold.DatabaseBootstrap.ScyllaReadinessOptions(TimeSpan.FromMinutes(5)),
            logger,
            ct);
    }
}

