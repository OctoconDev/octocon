extern alias AppHost;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Interfold.DatabaseBootstrap;
using Interfold.Infrastructure.Postgres;
using Interfold.Infrastructure.Scylla;
using Interfold.IntegrationTests.Shared.TestServices.TestBench;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Aspire;

namespace Interfold.IntegrationTests.Shared.TestServices;

/// <summary>Session-shared Aspire host for every DB-bound integration test. Postgres always
/// runs (internal.secrets is the shared source of truth); Scylla/Cassandra spin up only when
/// <see cref="RequiredFixtures"/> reports a dependent test in the session. Migrations run once
/// here; the per-test <see cref="InterfoldWebApplicationFactory"/> strips the migration
/// hosted services so they don't replay on every rebuild.</summary>
public sealed class SharedDbFixture : AspireFixture<AppHost::Projects.Interfold_AppHost>
{
    // Bench-mode short-circuit — when the current test host is opted in via
    // `INTERFOLD_TEST_BENCH=1`, the fixture attaches to the auto-managed cross-project
    // bench (`TestBenchCoordinator`) instead of booting an in-process AppHost. Legacy CI
    // legs and any local run without the env var take the original AspireFixture path.
    private readonly bool _benchMode = TestBenchCoordinator.IsOptedIn();

    /// <summary>Postgres connection string; always populated after startup.</summary>
    public string PostgresConnectionString { get; private set; } = string.Empty;

    /// <summary>Host port for the ScyllaDB CQL endpoint; null when Scylla wasn't required.</summary>
    public int? ScyllaPort { get; private set; }

    /// <summary>Host port for the Cassandra CQL endpoint; null when Cassandra wasn't required.</summary>
    public int? CassandraPort { get; private set; }

    /// <summary>Captured Scylla init failure (container / probe / seed / migration). The
    /// per-backend web-factory fixture rethrows this so only Scylla-dependent tests fail —
    /// Cassandra-only and InMemory-only tests survive.</summary>
    public Exception? ScyllaInitException { get; private set; }

    /// <summary>Cassandra mirror of <see cref="ScyllaInitException"/>.</summary>
    public Exception? CassandraInitException { get; private set; }

    protected override string[] Args => BuildArgs();

    private static string[] BuildArgs()
    {
        // BaseEndpointTest's [Before(TestDiscovery)] hook populates RequiredFixtures before
        // AspireFixture.InitializeAsync (the only caller of this getter) runs.
        LifecycleProbe.Log("SharedDbFixture.BuildArgs");
        var args = new List<string>
        {
            $"{AppHostParameterKeys.IncludeApi}=false",
            $"{AppHostParameterKeys.IncludeWeb}=false",
            $"{AppHostParameterKeys.PersistentContainers}=false",
            $"{AppHostParameterKeys.IncludePostgres}=true",
            $"{AppHostParameterKeys.IncludeScylla}={BoolWire.ToWireValue(RequiredFixtures.NeedScylla)}",
            $"{AppHostParameterKeys.IncludeCassandra}={BoolWire.ToWireValue(RequiredFixtures.NeedCassandra)}",
            $"{AppHostParameterKeys.PortsPostgres}=14200",
            $"{AppHostParameterKeys.PortsScylla}=19042",
            $"{AppHostParameterKeys.PortsCassandra}=19043",
            $"{AppHostParameterKeys.PostgresUser}={TestDbCredentials.PostgresAppUser}",
            $"{AppHostParameterKeys.PostgresPassword}={TestDbCredentials.PostgresAppPassword}",
            // Pinned so the AppHost's GenerateParameterDefault output can't drift from DbInitHelper.
            $"{AppHostParameterKeys.PostgresInitPassword}={TestDbCredentials.PostgresInitPassword}",
            // Route both sides through DbInitHelper.DefaultPostgresDb so a rename moves in lockstep.
            $"{AppHostParameterKeys.PostgresDb}={DbInitHelper.DefaultPostgresDb}",
            $"{AppHostParameterKeys.ScyllaUser}={TestDbCredentials.ScyllaAppUser}",
            $"{AppHostParameterKeys.ScyllaPassword}={TestDbCredentials.ScyllaAppPassword}",
            $"{AppHostParameterKeys.EncryptionPrivateKey}=TEST",
        };
        return args.ToArray();
    }

    protected override TimeSpan ResourceTimeout => TimeSpan.FromMinutes(5);
    protected override bool EnableTelemetryCollection => false;

    /// <summary>Per-container readiness budget so one stuck backend can't starve the other of
    /// the outer <see cref="ResourceTimeout"/>.</summary>
    private static readonly TimeSpan PerContainerReadyTimeout = TimeSpan.FromMinutes(2);

    // No Aspire-level healthchecks fire in test mode (include-api=false), and the default
    // AllHealthy behaviour would hang on parameter resources. WaitForResourcesAsync owns
    // readiness directly via ResourceNotificationService.
    protected override ResourceWaitBehavior WaitBehavior => ResourceWaitBehavior.None;

    public override async Task InitializeAsync()
    {
        if (_benchMode)
        {
            await InitializeBenchModeAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        // Raise fs.aio-max-nr session-wide (this fixture + optional MultiNodeScyllaFixture)
        // before any Scylla node starts; MultiNode re-asserts and short-circuits via cache.
        await HostAioPrerequisite
            .EnsureAsync(HostAioPrerequisite.TotalScyllaNodesForSession())
            .ConfigureAwait(false);
        await base.InitializeAsync().ConfigureAwait(false);
    }

    /// <summary>Bench-mode alternative to <c>base.InitializeAsync</c> +
    /// <see cref="WaitForResourcesAsync"/>. Delegates container lifecycle to
    /// <see cref="TestBenchCoordinator"/>, then runs the same seed + migration pipeline
    /// against the already-running containers. Migrations are idempotent, so a hot attach
    /// re-runs them harmlessly.</summary>
    private async Task InitializeBenchModeAsync(CancellationToken cancellationToken)
    {
        LifecycleProbe.Log("SharedDbFixture.InitializeBenchMode");

        // Raise the fs.aio-max-nr sysctl before Scylla starts, same as the legacy path.
        await HostAioPrerequisite
            .EnsureAsync(HostAioPrerequisite.TotalScyllaNodesForSession())
            .ConfigureAwait(false);

        var bench = await TestBenchCoordinator.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);

        // Bench always brings up all three engines — cross-project isolation is shared-global
        // (docs/test-bench-audit.md), so per-project RequiredFixtures flags no longer gate
        // which containers exist. Expose both CQL ports; per-backend factories pick one.
        ScyllaPort = bench.ScyllaPort;
        CassandraPort = bench.CassandraPort;
        PostgresConnectionString = bench.PostgresConnectionStringForApp;

        // Postgres readiness + seed + migration. Postgres is foundational (internal.secrets),
        // so any failure here is a session-wide problem; propagate instead of capturing.
        await DbInitHelper.WaitForPostgresAsync(
            bench.PostgresConnectionStringForInit,
            new PostgresReadinessOptions(TimeSpan.FromMinutes(6), 3),
            cancellationToken).ConfigureAwait(false);
        // SeedPostgresAsync runs `CREATE ROLE` / `CREATE DATABASE` / seed writes that
        // are individually idempotent but NOT concurrency-safe: peer projects racing
        // the same cold-boot bench all pass AppRoleAlreadyConfiguredAsync at once and
        // then collide on `CREATE DATABASE test_pg_db` (23505 unique_violation on
        // pg_database_datname_index). ApplyOnceAsync serialises the whole seed behind
        // the bench's file lock so only the first arrival runs it; hot-attach observers
        // skip via the "seed:postgres" marker.
        await TestBenchCoordinator.ApplyOnceAsync(
            "seed:postgres",
            () => DbInitHelper.SeedPostgresAsync(
                bench.PostgresConnectionStringForInit,
                BuildPostgresSeedOptions(cqlEndpoint: null),
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
        await WaitForPostgresConnectivityAsync(PostgresConnectionString, cancellationToken).ConfigureAwait(false);

        var persistenceConfig = new PersistenceConfiguration
        {
            Mode = Interfold.Shared.Contracts.PersistenceMode.ScyllaPostgres,
            PostgresConnectionString = PostgresConnectionString,
            IsSingleScyllaInstance = true,
            ScyllaKeyspace = ScyllaKeyspace.Nam,
        };
        var connectionFactory = new PostgresConnectionFactory(Microsoft.Extensions.Options.Options.Create(persistenceConfig));
        var secretsStore = new PostgresSecretsStore(connectionFactory);

        // ApplyOnceAsync serialises this migration behind the bench's file lock so
        // concurrent test-host inits never step on each other, and MigrationLedgerTests'
        // deliberate checksum-drift can't collide with a peer project's fixture init.
        // See docs/test-bench-audit.md.
        //
        // PostgresMigrationService.MigrateAsync silently returns when
        // `secretsStore.GetAsync(PostgresAdmin{Username,Password})` yields empty (production
        // dev-mode intent). Under bench mode that silent no-op is catastrophic: the
        // ApplyOnceAsync marker flips to `true` on a clean return, every subsequent
        // hot-attach skips migrations, and the first test hitting the shared bench 500s
        // with `42P01: relation "octocon_idempotency" does not exist`. Verify the ledger
        // exists post-migrate so a silent skip surfaces as a fail-fast here instead.
        await TestBenchCoordinator.ApplyOnceAsync(
            "postgres",
            async () =>
            {
                await PostgresMigrationService.MigrateAsync(
                    persistenceConfig,
                    secretsStore,
                    NullLoggerFactory.Instance.CreateLogger<PostgresMigrationService>(),
                    cancellationToken).ConfigureAwait(false);
                await VerifyPostgresLedgerAppliedAsync(bench.PostgresConnectionStringForInit, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        // Scylla + Cassandra: capture per-engine so a wedged CQL backend only breaks
        // fixtures that consume it. Mirrors the legacy path's per-backend try/catch.
        ScyllaInitException = await TryInitialiseBenchCqlAsync(
            "scylla", "127.0.0.1", bench.ScyllaPort, persistenceConfig, secretsStore, cancellationToken).ConfigureAwait(false);
        CassandraInitException = await TryInitialiseBenchCqlAsync(
            "cassandra", "127.0.0.1", bench.CassandraPort, persistenceConfig, secretsStore, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Exception?> TryInitialiseBenchCqlAsync(
        string backendLabel,
        string host,
        int port,
        PersistenceConfiguration persistenceConfig,
        ISecretsStore secretsStore,
        CancellationToken cancellationToken)
    {
        try
        {
            // The wait-for-CQL, seed, and migrate steps all live INSIDE ApplyOnceAsync so
            // the (default cassandra/cassandra) credentials WaitForScyllaAsync uses are only
            // exercised on cold-boot — right before the seeder scrambles that account. A
            // hot-attach observer sees the marker already set and skips the whole block; the
            // TCP-hot probe in TestBenchCoordinator.ArePortsHotAsync gated us in, so we
            // already know the CQL listener is alive.
            await TestBenchCoordinator.ApplyOnceAsync(
                $"cql-seed-and-migrate:{backendLabel}",
                async () =>
                {
                    await DbInitHelper.WaitForScyllaAsync(host, port, cancellationToken).ConfigureAwait(false);
                    await DbInitHelper.SeedScyllaAsync(host, port, BuildScyllaSeedOptions(), cancellationToken).ConfigureAwait(false);
                    var endpoint = new Uri($"tcp://{host}:{port}");
                    await RunScyllaMigrationsAsync(endpoint, persistenceConfig, secretsStore, cancellationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            LifecycleProbe.Log($"SharedDbFixture.BenchCqlInitFailed:{backendLabel}");
            return ex;
        }
    }

    public override async ValueTask DisposeAsync()
    {
        using var _ = LifecycleProbe.BeginTimed("AfterFixtureDispose:SharedDbFixture");
        LifecycleProbe.Log("BeforeFixtureDispose:SharedDbFixture");

        if (_benchMode)
        {
            // Bench containers survive this test host — teardown is the reaper's job on
            // the next attach. All we do is drop our PID from the users list.
            await TestBenchCoordinator.DetachAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        try
        {
            await AspireContainerCleanup
                .StopResourcesAsync(AspireContainerCleanup.SharedDbResourceNames())
                .ConfigureAwait(false);
        }
        catch
        {
            // Best-effort pre-stop; base dispose must still run.
        }

        await base.DisposeAsync().ConfigureAwait(false);

        var remaining = await AspireContainerCleanup.CountRunningAspireContainersAsync().ConfigureAwait(false);
        if (remaining > 0)
            LifecycleProbe.Log($"SharedDbFixture.RemainingAspireContainers:{remaining}");
    }

    protected override async Task WaitForResourcesAsync(DistributedApplication app, CancellationToken cancellationToken)
    {
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();

        // Postgres is foundational (internal.secrets); failures propagate. Wait for Running,
        // not Healthy — Aspire testing randomises ports so hardcoded-port checks never pass.
        await notifications.WaitForResourceAsync("msg-db", KnownResourceStates.Running, cancellationToken);

        var pgEndpoint = App.GetEndpoint("msg-db", "postgres");

        var initConnectionString =
            $"Host={pgEndpoint.Host};Port={pgEndpoint.Port};" +
            $"Username={DbInitHelper.PostgresInitUser};Password={TestDbCredentials.PostgresInitPassword};" +
            "Database=postgres;SSL Mode=Disable";
        await DbInitHelper.WaitForPostgresAsync(
            initConnectionString,
            new Interfold.DatabaseBootstrap.PostgresReadinessOptions(TimeSpan.FromMinutes(6), 3),
            cancellationToken);
        // Seed also writes the Scylla contact-point row; localhost placeholder is only
        // observable when the run is already broken before this point.
        await DbInitHelper.SeedPostgresAsync(
            initConnectionString,
            BuildPostgresSeedOptions(cqlEndpoint: null),
            cancellationToken);

        PostgresConnectionString =
            $"Host={pgEndpoint.Host};Port={pgEndpoint.Port};" +
            $"Username={TestDbCredentials.PostgresAppUser};Password={TestDbCredentials.PostgresAppPassword};" +
            $"Database={DbInitHelper.DefaultPostgresDb};SSL Mode=Disable;Maximum Pool Size=5";

        // Docker Desktop on Windows can delay host port forwarding even after healthy;
        // hard-fail here rather than during the first test.
        await WaitForPostgresConnectivityAsync(PostgresConnectionString, cancellationToken);

        // Single session-wide Postgres migration pass; per-test factories strip the hosted service.
        var persistenceConfig = new PersistenceConfiguration
        {
            Mode = Interfold.Shared.Contracts.PersistenceMode.ScyllaPostgres,
            PostgresConnectionString = PostgresConnectionString,
            IsSingleScyllaInstance = true,
            ScyllaKeyspace = ScyllaKeyspace.Nam,
        };
        var connectionFactory = new PostgresConnectionFactory(Microsoft.Extensions.Options.Options.Create(persistenceConfig));
        var secretsStore = new PostgresSecretsStore(connectionFactory);

        await PostgresMigrationService.MigrateAsync(
            persistenceConfig,
            secretsStore,
            NullLoggerFactory.Instance.CreateLogger<PostgresMigrationService>(),
            cancellationToken);

        // Each backend's init is captured into *InitException so only the per-backend
        // web-factory fixture surfaces it; the other backend and InMemory keep running.
        if (RequiredFixtures.NeedScylla)
        {
            ScyllaInitException = await TryInitialiseCqlBackendAsync(
                resourceName: "scylla",
                endpointName: "cql",
                notifications: notifications,
                persistenceConfig: persistenceConfig,
                secretsStore: secretsStore,
                assignPort: port => ScyllaPort = port,
                cancellationToken: cancellationToken);
        }

        if (RequiredFixtures.NeedCassandra)
        {
            CassandraInitException = await TryInitialiseCqlBackendAsync(
                resourceName: "cassandra",
                endpointName: "cql",
                notifications: notifications,
                persistenceConfig: persistenceConfig,
                secretsStore: secretsStore,
                assignPort: port => CassandraPort = port,
                cancellationToken: cancellationToken);
        }
    }

    /// <summary>Container-wait + probe + seed + migration for one CQL backend. Never throws;
    /// returns null on success or the captured exception on failure so the outer method can
    /// attribute per backend.</summary>
    private async Task<Exception?> TryInitialiseCqlBackendAsync(
        string resourceName,
        string endpointName,
        ResourceNotificationService notifications,
        PersistenceConfiguration persistenceConfig,
        ISecretsStore secretsStore,
        Action<int> assignPort,
        CancellationToken cancellationToken)
    {
        try
        {
            await WaitForContainerRunningAsync(notifications, resourceName, cancellationToken).ConfigureAwait(false);

            var endpoint = App.GetEndpoint(resourceName, endpointName);
            await DbInitHelper.WaitForScyllaAsync(endpoint.Host, endpoint.Port, cancellationToken).ConfigureAwait(false);
            await DbInitHelper.SeedScyllaAsync(
                endpoint.Host, endpoint.Port,
                BuildScyllaSeedOptions(),
                cancellationToken).ConfigureAwait(false);
            await RunScyllaMigrationsAsync(endpoint, persistenceConfig, secretsStore, cancellationToken).ConfigureAwait(false);
            assignPort(endpoint.Port);
            return null;
        }
        catch (Exception ex)
        {
            // Swallow OCE too — otherwise the base AspireFixture recategorises it as a
            // session-wide timeout and fails every test in the run.
            return ex;
        }
    }

    /// <summary>Waits for Running, racing against FailedToStart and
    /// <see cref="PerContainerReadyTimeout"/>.</summary>
    private async Task WaitForContainerRunningAsync(
        ResourceNotificationService notifications,
        string resourceName,
        CancellationToken outerCancellationToken)
    {
        using var perResourceCts = CancellationTokenSource.CreateLinkedTokenSource(outerCancellationToken);
        perResourceCts.CancelAfter(PerContainerReadyTimeout);
        var token = perResourceCts.Token;

        var readyTask = notifications.WaitForResourceAsync(
            resourceName, KnownResourceStates.Running, token);
        var failedTask = notifications.WaitForResourceAsync(
            resourceName, KnownResourceStates.FailedToStart, token);

        try
        {
            var completed = await Task.WhenAny(readyTask, failedTask).ConfigureAwait(false);
            if (completed == failedTask)
            {
                // Observe first so any carried exception surfaces, then throw a domain error.
                await failedTask.ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"Aspire resource '{resourceName}' entered FailedToStart before reaching Running. " +
                    "Check the AppHost container logs for the underlying error.");
            }

            await readyTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (perResourceCts.IsCancellationRequested && !outerCancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Aspire resource '{resourceName}' did not reach Running within " +
                $"{PerContainerReadyTimeout.TotalSeconds:0}s. The container is either still " +
                "pulling an image, failing to build (e.g. missing 'docker buildx'), or stuck " +
                "in a restart loop — inspect the AppHost container logs for details.");
        }
    }

    private static async Task RunScyllaMigrationsAsync(
        Uri cqlEndpoint,
        PersistenceConfiguration persistenceConfig,
        ISecretsStore secretsStore,
        CancellationToken cancellationToken)
    {
        // Pin the host-mapped endpoint so migrations hit the same CQL listener the API uses.
        var overrides = new ScyllaOverrideOptions
        {
            ContactPoints = [cqlEndpoint.Host],
            Port = cqlEndpoint.Port,
        };
        var resolver = new ScyllaConfigResolver(
            secretsStore,
            Microsoft.Extensions.Options.Options.Create(overrides),
            Microsoft.Extensions.Options.Options.Create(persistenceConfig));

        await ScyllaMigrationService.MigrateAsync(
            persistenceConfig,
            secretsStore,
            resolver,
            NullLoggerFactory.Instance.CreateLogger<ScyllaMigrationService>(),
            cancellationToken);
    }

    private static PostgresSeedOptions BuildPostgresSeedOptions(Uri? cqlEndpoint)
        => new(
            InitUser: DbInitHelper.PostgresInitUser,
            InitPassword: TestDbCredentials.PostgresInitPassword,
            AppUser: TestDbCredentials.PostgresAppUser,
            AppPassword: TestDbCredentials.PostgresAppPassword,
            AdminUser: TestDbCredentials.PostgresAdminUser,
            AdminPassword: TestDbCredentials.PostgresAdminPassword,
            DefaultDatabase: DbInitHelper.DefaultPostgresDb,
            GoogleOAuthClientSecret: "TEST",
            DiscordOAuthClientSecret: "TEST",
            AppleOAuthClientSecret: "TEST",
            EncryptionPepper: "TEST",
            // API runs on the host and reaches scylla/cassandra via the host port mapping;
            // localhost placeholder keeps the row well-formed when no CQL backend is enabled.
            ScyllaContactPoints: cqlEndpoint?.Host ?? "127.0.0.1",
            ScyllaLocalDatacenter: "nam",
            ScyllaAppUser: TestDbCredentials.ScyllaAppUser,
            ScyllaAppPassword: TestDbCredentials.ScyllaAppPassword,
            ScyllaPort: cqlEndpoint?.Port ?? 9042,
            ScyllaAdminUser: TestDbCredentials.ScyllaAdminUser,
            ScyllaAdminPassword: TestDbCredentials.ScyllaAdminPassword,
            JwtRsa256PrivateKeyPem: TestDbCredentials.JwtRsa256PrivateKeyPem,
            JwtEs256PrivateKeyPem: TestDbCredentials.JwtEs256PrivateKeyPem,
            DeepLinkSecret: TestDbCredentials.DeepLinkSecret,
            LeafPfxPassword: TestDbCredentials.LeafPfxPassword,
            // Tests want db_init stable across idempotent reruns; production always scrambles.
            ScrambleInitUserPassword: false);

    private static ScyllaSeedOptions BuildScyllaSeedOptions()
        => new(
            AppUser: TestDbCredentials.ScyllaAppUser,
            AppPassword: TestDbCredentials.ScyllaAppPassword,
            AdminUser: TestDbCredentials.ScyllaAdminUser,
            AdminPassword: TestDbCredentials.ScyllaAdminPassword,
            LockDefaultCassandra: true);

    private static async Task WaitForPostgresConnectivityAsync(string connectionString, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync(ct);
                return;
            }
            catch (Exception) when (attempt < 20 && !ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
            }
        }
    }

    // Catches the PostgresMigrationService.MigrateAsync silent-skip trap: it returns
    // cleanly when admin creds are absent from the secrets store, ApplyOnceAsync then
    // marks migrations as applied on a return without an exception, and every subsequent
    // hot-attach observer skips the migration too. The ledger table is created inside
    // MigrateAsync's EnsureLedgerAsync, so its absence is a strict signal that no
    // migration passed through, regardless of admin-creds or connection-string mistakes.
    // Reads via db_init because SELECT on internal.schema_migrations is only granted to
    // the admin role, and swaps the database to test_pg_db because the bench init string
    // targets `postgres` for role/database bootstrapping.
    private static async Task VerifyPostgresLedgerAppliedAsync(string initConnectionString, CancellationToken ct)
    {
        var verifyCs = new NpgsqlConnectionStringBuilder(initConnectionString)
        {
            Database = DbInitHelper.DefaultPostgresDb,
        }.ConnectionString;
        await using var conn = new NpgsqlConnection(verifyCs);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var existsCmd = new NpgsqlCommand(
            "SELECT to_regclass('internal.schema_migrations') IS NOT NULL", conn);
        var existsResult = await existsCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        var tableExists = existsResult is bool b && b;
        var rowCount = 0L;
        if (tableExists)
        {
            await using var countCmd = new NpgsqlCommand(
                "SELECT count(*) FROM internal.schema_migrations", conn);
            rowCount = (long)(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        }
        if (!tableExists || rowCount == 0)
        {
            throw new InvalidOperationException(
                $"SharedDbFixture: Postgres migrations did not populate internal.schema_migrations " +
                $"(tableExists={tableExists}, rowCount={rowCount}). This usually means " +
                "PostgresMigrationService.MigrateAsync short-circuited on missing admin " +
                "credentials (PostgresAdmin{Username,Password} in internal.secrets); the " +
                "ApplyOnceAsync marker would then strand every hot-attach on a schema-less DB.");
        }
    }
}
