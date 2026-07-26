extern alias AppHost;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Cassandra;
using Interfold.DatabaseBootstrap;
using Interfold.Infrastructure.Postgres;
using Interfold.Infrastructure.Scylla;
using Interfold.IntegrationTests.Shared.TestServices;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Aspire;

namespace Interfold.Infrastructure.IntegrationTests.TestServices;

/// <summary>7-node multi-DC ScyllaDB cluster in its own Aspire host, sharing Postgres +
/// seeded internal.secrets with <see cref="SharedDbFixture"/> via a ClassDataSource dependency
/// so multi-DC migrations see the same credentials as the single-node runs.</summary>
public sealed class MultiNodeScyllaFixture : AspireFixture<AppHost::Projects.Interfold_AppHost>
{
    /// <summary>Session-shared dependency that owns Postgres + secrets seeding.</summary>
    [ClassDataSource<SharedDbFixture>(Shared = SharedType.PerTestSession)]
    public required SharedDbFixture SharedDb { get; init; }

    /// <summary>Host port for the first Scylla node's CQL endpoint (scylla-nam).</summary>
    public int ScyllaPort { get; private set; }

    protected override string[] Args =>
    [
        $"{AppHostParameterKeys.IncludePostgres}=false",
        $"{AppHostParameterKeys.IncludeScylla}=true",
        $"{AppHostParameterKeys.IncludeCassandra}=false",
        $"{AppHostParameterKeys.ScyllaTopology}={ScyllaTopologyExtensions.MultiWireValue}",
        $"{AppHostParameterKeys.IncludeApi}=false",
        $"{AppHostParameterKeys.IncludeWeb}=false",
        $"{AppHostParameterKeys.PersistentContainers}=false",
        // Distinct port so Aspire test-mode allocator doesn't collide with SharedDbFixture.
        $"{AppHostParameterKeys.PortsScylla}=39042",
        $"{AppHostParameterKeys.ScyllaUser}={TestDbCredentials.ScyllaAppUser}",
        $"{AppHostParameterKeys.ScyllaPassword}={TestDbCredentials.ScyllaAppPassword}",
        $"{AppHostParameterKeys.EncryptionPrivateKey}=TEST"
    ];

    // 15 min covers slowest legitimate path: gossip bootstrap + sequential per-node CQL health
    // gating (~30-60s per non-seed node × 6) + migrations.
    protected override TimeSpan ResourceTimeout => TimeSpan.FromMinutes(15);
    protected override bool EnableTelemetryCollection => false;

    // No Aspire health checks fire under include-api=false; readiness is driven directly
    // via ResourceNotificationService in WaitForResourcesAsync.
    protected override ResourceWaitBehavior WaitBehavior => ResourceWaitBehavior.None;

    public override async Task InitializeAsync()
    {
        // Raise fs.aio-max-nr session-wide before any Scylla node starts; without it the
        // 3rd container onwards fails seastar AIO init and the cluster stalls at 2 nodes.
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

        // Seed via default cassandra/cassandra; identical creds to SharedDbFixture so the
        // migration step below authenticates with the same admin row in internal.secrets.
        var scEndpoint = App.GetEndpoint("scylla-nam", "cql");
        await DbInitHelper.WaitForScyllaAsync(scEndpoint.Host, scEndpoint.Port, cancellationToken);
        await DbInitHelper.SeedScyllaAsync(
            scEndpoint.Host, scEndpoint.Port, BuildScyllaSeedOptions(), cancellationToken);

        // Container Running only means the CQL listener bound; gossip + range streaming
        // finish asynchronously and NetworkTopologyStrategy DDL needs cross-DC quorum first.
        await WaitForGossipPropagationAsync(scEndpoint, regions, cancellationToken);

        // Migrations use SharedDbFixture's internal.secrets creds but the multi-DC endpoint.
        var persistenceConfig = new PersistenceConfiguration
        {
            Mode = Interfold.Shared.Contracts.PersistenceMode.ScyllaPostgres,
            PostgresConnectionString = SharedDb.PostgresConnectionString,
            IsSingleScyllaInstance = false,
            ScyllaKeyspace = ScyllaKeyspace.Nam,
        };
        var connectionFactory = new PostgresConnectionFactory(Microsoft.Extensions.Options.Options.Create(persistenceConfig));
        var secretsStore = new PostgresSecretsStore(connectionFactory);

        var overrides = new ScyllaOverrideOptions
        {
            ContactPoints = [scEndpoint.Host],
            Port = scEndpoint.Port,
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

        ScyllaPort = scEndpoint.Port;
    }

    private static async Task WaitForGossipPropagationAsync(
        Uri scEndpoint,
        string[] expectedRegions,
        CancellationToken cancellationToken)
    {
        // 5 min covers slowest DinD gossip (30-45 s/node) within the outer ResourceTimeout.
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        Exception? lastError = null;
        HashSet<string> lastObserved = new(StringComparer.OrdinalIgnoreCase);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using var sts = await ScyllaTestSession.OpenAsAppAsync(
                    scEndpoint.Host, scEndpoint.Port, queryTimeoutMs: 15000);
                var session = sts.Session;

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
