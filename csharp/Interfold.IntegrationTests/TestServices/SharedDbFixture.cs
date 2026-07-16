extern alias AppHost;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Interfold.Contracts;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Secrets;
using Interfold.DatabaseBootstrap;
using Interfold.Infrastructure.Postgres;
using Interfold.Infrastructure.Scylla;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Aspire;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// Single Aspire host that backs every DB-bound integration test in the suite. Replaces the
/// previous per-fixture hosts (<c>SingleNodeScyllaFixture</c>, <c>CassandraFixture</c>) with
/// one shared launch so cold-start, seeding, and migrations only run once per test session.
/// </summary>
/// <remarks>
/// <para>
/// Conditionally provisions resources based on which <see cref="IWebFactoryFixture"/>
/// implementations the current test session actually references (discovered ahead of time by
/// <see cref="RequiredFixtures.DiscoverRequiredFixtures"/>). Postgres always runs because the
/// shared <c>internal.secrets</c> table is the API's source of truth even for the in-memory
/// persistence path. Scylla and Cassandra only spin up when at least one selected test class
/// references their respective fixture — for an InMemory-only run this fixture isn't even
/// instantiated and Docker is never touched.
/// </para>
/// <para>
/// Migrations against each enabled CQL backend run once here (after seeding) via the static
/// <c>MigrateAsync</c> entry points on <see cref="ScyllaMigrationService"/> and
/// <see cref="PostgresMigrationService"/>. The per-test
/// <see cref="InterfoldWebApplicationFactory"/> strips those hosted services from its host
/// builder so the same migrations don't run again on every factory build.
/// </para>
/// </remarks>
public sealed class SharedDbFixture : AspireFixture<AppHost::Projects.Interfold_AppHost>
{
    /// <summary>Postgres connection string resolved after startup. Always populated.</summary>
    public string PostgresConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// Host port for the ScyllaDB CQL endpoint, or <c>null</c> when the discovery hook
    /// determined no test in the current run uses the Scylla fixture chain.
    /// </summary>
    public int? ScyllaPort { get; private set; }

    /// <summary>
    /// Host port for the Cassandra CQL endpoint, or <c>null</c> when the discovery hook
    /// determined no test in the current run uses the Cassandra fixture chain.
    /// </summary>
    public int? CassandraPort { get; private set; }

    /// <summary>
    /// Non-null when the Scylla container failed to reach Running, the host-level CQL
    /// connectivity probe failed, or the Scylla seed/migration sequence threw during fixture
    /// initialization. <see cref="ScyllaWebFactoryFixture"/> rethrows this from its own
    /// <c>InitializeAsync</c> so only the tests that actually depend on Scylla fail when this
    /// one backend is broken — Cassandra-only and InMemory-only tests in the same run keep
    /// passing instead of inheriting the Scylla failure transitively through this fixture.
    /// </summary>
    public Exception? ScyllaInitException { get; private set; }

    /// <summary>
    /// Mirror of <see cref="ScyllaInitException"/> for the Cassandra backend. See that
    /// property's remarks for the rationale (per-backend failure attribution instead of
    /// fail-the-whole-suite when one container can't start).
    /// </summary>
    public Exception? CassandraInitException { get; private set; }

    protected override string[] Args => BuildArgs();

    private static string[] BuildArgs()
    {
        // Toggle each container based on which fixture types the current test session
        // references. The [Before(HookType.TestDiscovery)] hook on BaseEndpointTest has
        // populated RequiredFixtures by the time AspireFixture.InitializeAsync (the only
        // caller of this Args getter) runs.
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
            // db_init bootstrap superuser password. Pinning a deterministic value here keeps
            // the test process and DbInitHelper aligned without having to read back the
            // GenerateParameterDefault output from the AppHost service provider.
            $"{AppHostParameterKeys.PostgresInitPassword}={TestDbCredentials.PostgresInitPassword}",
            // Pin the application database name so the AppHost's `Parameters:postgres-db`
            // default and the in-process DbInitHelper.DefaultPostgresDb cannot silently diverge
            // (e.g. if someone changes the AppHost default later). Both currently resolve to
            // "interfold"; routing both through the same constant means a single rename moves
            // them in lockstep.
            $"{AppHostParameterKeys.PostgresDb}={DbInitHelper.DefaultPostgresDb}",
            $"{AppHostParameterKeys.ScyllaUser}={TestDbCredentials.ScyllaAppUser}",
            $"{AppHostParameterKeys.ScyllaPassword}={TestDbCredentials.ScyllaAppPassword}",
            $"{AppHostParameterKeys.EncryptionPrivateKey}=TEST",
        };
        return args.ToArray();
    }

    protected override TimeSpan ResourceTimeout => TimeSpan.FromMinutes(5);
    protected override bool EnableTelemetryCollection => false;

    /// <summary>
    /// Per-container readiness budget enforced in addition to the outer
    /// <see cref="ResourceTimeout"/>. Without this each individual
    /// <c>WaitForResourceAsync(name, Running)</c> would happily eat the full 5-minute outer
    /// budget on its own (a Scylla container that never reports Running starves the Cassandra
    /// wait that follows it, and vice-versa). 2 minutes gives a comfortably loaded Docker
    /// host enough headroom for image pulls + container start while still surfacing a
    /// permanently-broken backend (e.g. missing <c>docker buildx</c>, image-pull auth
    /// failure) as a clean per-backend timeout instead of a session-wide TimeoutException.
    /// </summary>
    private static readonly TimeSpan PerContainerReadyTimeout = TimeSpan.FromMinutes(2);

    // The AppHost only registers TCP healthchecks against `localhost:<hard-coded port>` when
    // `include-api=true`, so test mode has no Aspire-level health checks at all. The default
    // `AllHealthy` behaviour would also try to drive parameter resources (postgres-user, etc.)
    // through health gates - those never report "Healthy" and would hang the wait. We own the
    // readiness logic in <see cref="WaitForResourcesAsync"/> below (waiting on the actual
    // container resources to reach `Running` via `ResourceNotificationService`), so we tell
    // the base class not to do its own pre-flight wait.
    protected override ResourceWaitBehavior WaitBehavior => ResourceWaitBehavior.None;

    public override async Task InitializeAsync()
    {
        // Raise fs.aio-max-nr before any Scylla node starts. We pass the *session-wide*
        // total (this fixture's optional 1 + MultiNodeScyllaFixture's optional 7) so a mixed
        // session that runs both fixtures together gets sized for all 8 nodes from the first
        // call; MultiNodeScyllaFixture re-asserts the same total and short-circuits via
        // HostAioPrerequisite's cache. No-op when neither Scylla nor MultiNode is requested
        // (Cassandra-only / InMemory-only runs).
        await HostAioPrerequisite
            .EnsureAsync(HostAioPrerequisite.TotalScyllaNodesForSession())
            .ConfigureAwait(false);
        await base.InitializeAsync().ConfigureAwait(false);
    }

    public override async ValueTask DisposeAsync()
    {
        using var _ = LifecycleProbe.BeginTimed("AfterFixtureDispose:SharedDbFixture");
        LifecycleProbe.Log("BeforeFixtureDispose:SharedDbFixture");

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

        // Postgres is foundational: every IWebFactoryFixture (InMemory aside) reads from
        // internal.secrets, and the seed step below populates that table for both CQL backends
        // in one go. Any failure here is unrecoverable for the suite, so we let it propagate.
        // Wait for Running (not Healthy) — Aspire testing randomises host ports, so the
        // hardcoded-port health checks declared in AppHost never pass in test mode.
        await notifications.WaitForResourceAsync("msg-db", KnownResourceStates.Running, cancellationToken);

        var pgEndpoint = App.GetEndpoint("msg-db", "postgres");

        // The CQL endpoints are looked up lazily inside each backend's section, so a Cassandra
        // resource that never reached Running can't make us call App.GetEndpoint("cassandra")
        // (which throws InvalidOperationException for a never-started resource).
        var initConnectionString =
            $"Host={pgEndpoint.Host};Port={pgEndpoint.Port};" +
            $"Username={DbInitHelper.PostgresInitUser};Password={TestDbCredentials.PostgresInitPassword};" +
            "Database=postgres;SSL Mode=Disable";
        await DbInitHelper.WaitForPostgresAsync(initConnectionString, cancellationToken);
        // The seed writes the *Scylla* contact-point row into internal.secrets. We pass
        // localhost+default-port as a placeholder when neither backend is enabled (an
        // InMemory-only run never instantiates this fixture, so the placeholder only matters
        // when the run is fully broken before this point and we still want a well-formed row).
        await DbInitHelper.SeedPostgresAsync(
            initConnectionString,
            BuildPostgresSeedOptions(cqlEndpoint: null),
            cancellationToken);

        PostgresConnectionString =
            $"Host={pgEndpoint.Host};Port={pgEndpoint.Port};" +
            $"Username={TestDbCredentials.PostgresAppUser};Password={TestDbCredentials.PostgresAppPassword};" +
            $"Database={DbInitHelper.DefaultPostgresDb};SSL Mode=Disable;Maximum Pool Size=5";

        // Verify Postgres is actually reachable as the app user from the host before the
        // migration runner connects. Docker Desktop on Windows can delay host port forwarding
        // even after the container reports healthy and we want a hard failure here rather
        // than during the first test.
        await WaitForPostgresConnectivityAsync(PostgresConnectionString, cancellationToken);

        // Run the Postgres migrations once for the whole session. The per-test
        // InterfoldWebApplicationFactory strips PostgresMigrationService from its host so this
        // is the single migration pass against msg-db for the entire run.
        var persistenceConfig = new PersistenceConfiguration
        {
            Mode = Interfold.Contracts.PersistenceMode.ScyllaPostgres,
            PostgresConnectionString = PostgresConnectionString,
            IsSingleScyllaInstance = true,
            ScyllaKeyspace = ScyllaKeyspace.Nam,
        };
        var connectionFactory = new PostgresConnectionFactory(Options.Create(persistenceConfig));
        var secretsStore = new PostgresSecretsStore(connectionFactory);

        await PostgresMigrationService.MigrateAsync(
            persistenceConfig,
            secretsStore,
            NullLoggerFactory.Instance.CreateLogger<PostgresMigrationService>(),
            cancellationToken);

        // Each CQL backend gets its own try/catch around the container-wait + seed + migration
        // sequence. A failure is captured into the matching `*InitException` property so only
        // the per-backend web-factory fixture surfaces it (see ScyllaWebFactoryFixture and
        // CassandraWebFactoryFixture). Scylla-only test runs survive a broken cassandra
        // container, and vice versa, instead of every DB-bound test in the session failing
        // with the same generic AspireFixture timeout.
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

    /// <summary>
    /// Runs the full container-wait + connectivity-probe + seed + migration sequence for one
    /// CQL backend, returning <c>null</c> on success or the captured exception on failure.
    /// Never throws so the caller can attribute the failure to a single backend without
    /// aborting the rest of <see cref="WaitForResourcesAsync"/>.
    /// </summary>
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
            // Swallow everything — including OperationCanceledException tied to the outer
            // ResourceTimeout. Letting OCE propagate would let the base AspireFixture
            // recategorise it as a session-wide "Timed out after Xs waiting for Aspire
            // resources" exception and re-fail every test in the session.
            return ex;
        }
    }

    /// <summary>
    /// Waits for <paramref name="resourceName"/> to reach Running, racing the wait against a
    /// transition into FailedToStart (immediate failure) and a per-container readiness budget
    /// from <see cref="PerContainerReadyTimeout"/> (so a permanently-stuck container can't
    /// starve any other resource of the outer <see cref="ResourceTimeout"/>).
    /// </summary>
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
                // Observe the completed task (so any exception it carries is surfaced rather
                // than being silently lost), then throw a domain-specific error the captured-
                // exception machinery above can attribute to this backend.
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
        // Build the resolver with the host-mapped endpoint pinned via ScyllaOverrideOptions
        // so the migration runs against the exact CQL listener the API will hit during the
        // test. The keyspace is read straight off persistenceConfig.ScyllaKeyspace (single
        // source of truth) — no throwaway IConfiguration needed anymore.
        var overrides = new ScyllaOverrideOptions
        {
            ContactPoints = [cqlEndpoint.Host],
            Port = cqlEndpoint.Port,
        };
        var resolver = new ScyllaConfigResolver(
            secretsStore,
            Options.Create(overrides),
            Options.Create(persistenceConfig));

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
            // The API process runs on the test host and reaches scylla/cassandra via the host
            // port mapping, so contact_points carries the resolved endpoint host. When neither
            // backend is enabled (InMemory-only sessions never hit this path because
            // SharedDbFixture isn't constructed), fall back to localhost so the seeded value
            // stays well-formed.
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
            // Tests want db_init's password stable across idempotent reruns within the same
            // fixture session, so we never scramble it. Production callers always set true.
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
}
