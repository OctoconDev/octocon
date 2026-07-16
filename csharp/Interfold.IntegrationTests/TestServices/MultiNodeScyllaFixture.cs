extern alias AppHost;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Cassandra;
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
using TUnit.Aspire;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// TUnit.Aspire fixture that manages a 7-node multi-DC ScyllaDB cluster via the AppHost,
/// running alongside the session-shared <see cref="SharedDbFixture"/> rather than spinning up
/// a duplicate Postgres instance.
/// </summary>
/// <remarks>
/// <para>
/// The fixture runs in its own Aspire host (separate AppHost args, separate port range) so the
/// 7-region Scylla topology doesn't collide with <see cref="SharedDbFixture"/>'s single-node
/// Scylla. We pass <c>Parameters:include-postgres=false</c> so the multi-DC host doesn't start
/// a redundant msg-db — the session-shared <see cref="SharedDbFixture"/> already owns the
/// canonical Postgres + seeded <c>internal.secrets</c> rows.
/// </para>
/// <para>
/// <see cref="ScyllaMigrationService.MigrateAsync"/> reads the Scylla admin credentials from
/// <c>internal.secrets</c> via <see cref="ISecretsStore"/>, so we depend on
/// <see cref="SharedDbFixture"/> through a <see cref="ClassDataSourceAttribute{T}"/> property
/// to guarantee that seed work is finished before this fixture's
/// <see cref="WaitForResourcesAsync"/> tries to migrate the multi-DC cluster. The dependency
/// also flips <see cref="RequiredFixtures.NeedMultiNodeScylla"/> on through the same
/// scheduled-test inspection that drives Scylla / Cassandra toggles.
/// </para>
/// <para>
/// Multi-node clusters take longer to gossip-bootstrap than the single-node fixture, so the
/// resource timeout is bumped to five minutes; the tests under <c>Topology/</c> are intentionally
/// run in their own scheduled set during CI to avoid prolonging the API-test critical path.
/// </para>
/// </remarks>
public sealed class MultiNodeScyllaFixture : AspireFixture<AppHost::Projects.Interfold_AppHost>
{
    /// <summary>
    /// Injected by TUnit so the SharedDbFixture (and its seeded <c>internal.secrets</c> rows)
    /// are guaranteed to be in place before <see cref="WaitForResourcesAsync"/> starts the
    /// multi-DC migration step. Not used at <see cref="Args"/>-evaluation time — that getter
    /// runs before property injection — so we keep <c>include-postgres=false</c> hardcoded.
    /// </summary>
    [ClassDataSource<SharedDbFixture>(Shared = SharedType.PerTestSession)]
    public required SharedDbFixture SharedDb { get; init; }

    /// <summary>Host port for the first Scylla node's CQL endpoint (scylla-nam).</summary>
    public int ScyllaPort { get; private set; }

    protected override string[] Args =>
    [
        // Drives a 7-region multi-DC Scylla layout in this fixture's own Aspire host. Postgres
        // lives in SharedDbFixture's host instead — see class-level remarks.
        $"{AppHostParameterKeys.IncludePostgres}=false",
        $"{AppHostParameterKeys.IncludeScylla}=true",
        $"{AppHostParameterKeys.IncludeCassandra}=false",
        $"{AppHostParameterKeys.ScyllaTopology}={ScyllaTopologyExtensions.MultiWireValue}",
        $"{AppHostParameterKeys.IncludeApi}=false",
        $"{AppHostParameterKeys.IncludeWeb}=false",
        $"{AppHostParameterKeys.PersistentContainers}=false",
        // Distinct port range so the Aspire host can run side-by-side with SharedDbFixture
        // without the Aspire test-mode port allocator complaining about reuse. SharedDbFixture
        // claims 14200 / 19042 / 19043; this fixture claims 39042.
        $"{AppHostParameterKeys.PortsScylla}=39042",
        // postgres-* parameters intentionally omitted — they're only consumed by the msg-db
        // container, which include-postgres=false skips entirely.
        $"{AppHostParameterKeys.ScyllaUser}={TestDbCredentials.ScyllaAppUser}",
        $"{AppHostParameterKeys.ScyllaPassword}={TestDbCredentials.ScyllaAppPassword}",
        $"{AppHostParameterKeys.EncryptionPrivateKey}=TEST"
    ];

    // Multi-DC Scylla startup itself takes ~2 minutes, then SeedScyllaAsync, the per-keyspace
    // migrations, and the gossip-readiness wait stack on top. The wrapper enforces this
    // timeout against the entire WaitForResourcesAsync override (not just the base
    // ResourceNotificationService wait), so we need enough budget for the slowest legitimate
    // path. With the per-node CQL health-check chain in the AppHost serialising joins (see
    // ScyllaContainerNameWatcher / DockerExecCqlProbe), each non-seed node only starts after
    // the previous node's CQL listener accepts — that adds ~30-60 s per non-seed node × 6
    // non-seed nodes ≈ +5 min on top of the previous ~5-minute steady state. 15 minutes
    // leaves headroom for the slowest legitimate sequential bring-up plus migration time.
    protected override TimeSpan ResourceTimeout => TimeSpan.FromMinutes(15);
    protected override bool EnableTelemetryCollection => false;

    // SharedDbFixture (and the AppHost's behaviour when include-api=false) provide no Aspire-
    // level health checks for the multi-DC cluster either, so AllHealthy would hang waiting on
    // resources that never report Healthy. We drive readiness via ResourceNotificationService
    // explicitly in WaitForResourcesAsync — same pattern as SharedDbFixture.
    protected override ResourceWaitBehavior WaitBehavior => ResourceWaitBehavior.None;

    public override async Task InitializeAsync()
    {
        // Raise fs.aio-max-nr before any Scylla node starts, sized for the full session
        // (this fixture's 7 nodes + SharedDbFixture's optional 1). SharedDbFixture also calls
        // EnsureAsync with the same total — whichever runs first sets the limit; the second
        // call short-circuits via HostAioPrerequisite's `_appliedFor` cache. Without this the
        // 3rd Scylla container onwards crashes with "Could not initialize seastar (...AIO)"
        // and the multi-DC cluster only ever reaches a 2-node gossip view.
        await HostAioPrerequisite
            .EnsureAsync(HostAioPrerequisite.TotalScyllaNodesForSession())
            .ConfigureAwait(false);
        await base.InitializeAsync().ConfigureAwait(false);
    }

    public override async ValueTask DisposeAsync()
    {
        using var _ = LifecycleProbe.BeginTimed("AfterFixtureDispose:MultiNodeScyllaFixture");
        LifecycleProbe.Log("BeforeFixtureDispose:MultiNodeScyllaFixture");

        try
        {
            await AspireContainerCleanup
                .StopResourcesAsync(AspireContainerCleanup.MultiNodeScyllaResourceNames())
                .ConfigureAwait(false);
        }
        catch
        {
            // Best-effort pre-stop; base dispose must still run.
        }

        await base.DisposeAsync().ConfigureAwait(false);

        var remaining = await AspireContainerCleanup.CountRunningAspireContainersAsync().ConfigureAwait(false);
        if (remaining > 0)
            LifecycleProbe.Log($"MultiNodeScyllaFixture.RemainingAspireContainers:{remaining}");
    }

    protected override async Task WaitForResourcesAsync(DistributedApplication app, CancellationToken cancellationToken)
    {
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();

        string[] regions = ["nam", "eur", "sam", "sas", "eas", "ocn", "gdpr"];
        foreach (var region in regions)
        {
            await notifications.WaitForResourceAsync($"scylla-{region}", KnownResourceStates.Running, cancellationToken);
        }

        // Seed the Scylla 7-DC cluster (creates test_admin_user + test_app_user, locks down
        // the default cassandra superuser). Independent of Postgres — uses the default
        // cassandra/cassandra entry point. SharedDbFixture's own SeedScyllaAsync ran the same
        // sequence against its single-node cluster; the credentials are identical so the
        // migration step below can authenticate with the admin creds stored in
        // SharedDbFixture's internal.secrets.
        var scEndpoint = App.GetEndpoint("scylla-nam", "cql");
        await DbInitHelper.WaitForScyllaAsync(scEndpoint.Host, scEndpoint.Port, cancellationToken);
        await DbInitHelper.SeedScyllaAsync(
            scEndpoint.Host, scEndpoint.Port, BuildScyllaSeedOptions(), cancellationToken);

        // Block until all seven DCs are visible from `nam`'s system.peers BEFORE we attempt
        // any NetworkTopologyStrategy DDL. Each Scylla container reports
        // KnownResourceStates.Running as soon as its CQL listener binds, but gossip-bootstrap
        // (the protocol that propagates peer + DC metadata across the cluster) and the
        // ranges-streaming bootstrap that follows finish asynchronously after that. Running
        // ScyllaMigrationService before gossip converges produces "host did not reply"
        // OperationTimedOutException because the migration's CREATE KEYSPACE statements
        // require quorum across DCs that the coordinator hasn't fully discovered yet.
        await WaitForGossipPropagationAsync(scEndpoint, regions, cancellationToken);

        // Run the Scylla migrations against the 7-DC cluster. ScyllaMigrationService reads the
        // admin credentials from internal.secrets (populated by SharedDbFixture's
        // SeedPostgresAsync) but takes the contact points / port via IConfiguration so we can
        // point it at this fixture's multi-DC endpoint instead of SharedDbFixture's single-node
        // one.
        var persistenceConfig = new PersistenceConfiguration
        {
            Mode = Interfold.Contracts.PersistenceMode.ScyllaPostgres,
            PostgresConnectionString = SharedDb.PostgresConnectionString,
            IsSingleScyllaInstance = false,
            ScyllaKeyspace = ScyllaKeyspace.Nam,
        };
        var connectionFactory = new PostgresConnectionFactory(Options.Create(persistenceConfig));
        var secretsStore = new PostgresSecretsStore(connectionFactory);

        var overrides = new ScyllaOverrideOptions
        {
            ContactPoints = [scEndpoint.Host],
            Port = scEndpoint.Port,
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

        ScyllaPort = scEndpoint.Port;
    }

    private static async Task WaitForGossipPropagationAsync(
        Uri scEndpoint,
        string[] expectedRegions,
        CancellationToken cancellationToken)
    {
        // 5 minutes leaves headroom for the slowest DinD environments where Scylla's gossip
        // bootstrap can take 30-45 seconds per node to fully propagate. The wrapper enforces
        // ResourceTimeout (currently 10 minutes) on this whole method, so the budget here plus
        // earlier seed/migration work needs to fit within that envelope.
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        Exception? lastError = null;
        HashSet<string> lastObserved = new(StringComparer.OrdinalIgnoreCase);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var cluster = Cluster.Builder()
                    .AddContactPoint(scEndpoint.Host)
                    .WithPort(scEndpoint.Port)
                    .WithLoadBalancingPolicy(new DCAwareRoundRobinPolicy("nam"))
                    .WithCredentials(TestDbCredentials.ScyllaAppUser, TestDbCredentials.ScyllaAppPassword)
                    .WithQueryTimeout(15000)
                    .Build();

                var session = await cluster.ConnectAsync();
                try
                {
                    var visibleDcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    var localRows = await session.ExecuteAsync(new SimpleStatement("SELECT data_center FROM system.local"));
                    foreach (var row in localRows)
                    {
                        var dc = row.GetValue<string>("data_center");
                        if (!string.IsNullOrWhiteSpace(dc))
                            visibleDcs.Add(dc.ToLowerInvariant());
                    }

                    var peerRows = await session.ExecuteAsync(new SimpleStatement("SELECT data_center FROM system.peers"));
                    foreach (var row in peerRows)
                    {
                        var dc = row.GetValue<string>("data_center");
                        if (!string.IsNullOrWhiteSpace(dc))
                            visibleDcs.Add(dc.ToLowerInvariant());
                    }

                    // Console.WriteLine here goes to TUnit's per-test capture, but this code
                    // runs in the fixture init phase before any test owns the async context, so
                    // the stdout is dropped. The message is still useful for ad-hoc diagnosis
                    // when running the tests outside `dotnet test` (e.g. IDE Test Explorer
                    // streams stdout live), so we keep it as a low-cost progress indicator.
                    if (!visibleDcs.SetEquals(lastObserved))
                    {
                        Console.WriteLine(
                            $"[multi-node-fixture {DateTime.UtcNow:HH:mm:ss}] gossip view: {string.Join(", ", visibleDcs.OrderBy(x => x))}");
                        lastObserved = new HashSet<string>(visibleDcs, StringComparer.OrdinalIgnoreCase);
                    }

                    if (expectedRegions.All(r => visibleDcs.Contains(r)))
                    {
                        return;
                    }

                    lastError = new InvalidOperationException(
                        $"Gossip not converged yet — saw {string.Join(", ", visibleDcs.OrderBy(x => x))}, " +
                        $"expected {string.Join(", ", expectedRegions)}.");
                }
                finally
                {
                    await session.ShutdownAsync();
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        throw new TimeoutException(
            "Multi-DC Scylla gossip did not converge across all 7 regions within the 5-minute readiness window. " +
            $"Last observation: {lastError?.Message}", lastError);
    }

    private static ScyllaSeedOptions BuildScyllaSeedOptions()
        => new(
            AppUser: TestDbCredentials.ScyllaAppUser,
            AppPassword: TestDbCredentials.ScyllaAppPassword,
            AdminUser: TestDbCredentials.ScyllaAdminUser,
            AdminPassword: TestDbCredentials.ScyllaAdminPassword,
            LockDefaultCassandra: true);
}
