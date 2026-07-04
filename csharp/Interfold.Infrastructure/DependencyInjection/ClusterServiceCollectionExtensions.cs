using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.ImportJobs;
using Interfold.Infrastructure.Coordination;

namespace Interfold.Infrastructure.DependencyInjection;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers node-role and cluster coordination services.
    /// Call this after <see cref="AddInterfoldPersistence"/> in <c>Program.cs</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="role">The resolved node group; pass <see cref="NodeGroupResolver.Resolve()"/>.</param>
    public static IServiceCollection AddInterfoldCluster(
        this IServiceCollection services,
        NodeGroup role)
    {
        // Node-role context — singleton read by any service that needs to know the role.
        services.AddSingleton<INodeRoleContext>(new NodeRoleContext(role));

        // Cluster event bus — in-process for single-node and integration tests.
        services.AddSingleton<IClusterEventBus, InProcessEventBus>();

        // Async-import queue — single-channel in-process FIFO consumed by
        // ImportJobBackgroundService. The per-system mutex lives in
        // IImportOperationRepository (LWT on active_import_by_system), not here, so a
        // single global channel + one worker is the simplest correct shape.
        services.AddSingleton<IImportJobQueue, InProcessImportJobQueue>();

        // FCM push notification service — both implementations are registered as their
        // concrete types so either can be selected at IFCMService resolution time
        // without conditional wiring.
        services.AddSingleton<NullFCMService>();
        services.AddSingleton<FirebaseFCMService>();

        // The factory decides which concrete class to hand out based on runtime state:
        //   1. Auxiliary / sidecar nodes never send — always the no-op.
        //   2. Primary nodes promote to FirebaseFCMService ONLY when the
        //      fcm:service_account_json row is seeded. An empty / missing row means the
        //      deployment hasn't wired Firebase and we keep behaving exactly as we did
        //      before this refactor (Debug-level no-op logging).
        //
        // Blocking GetAwaiter().GetResult() on ISecretsStore.GetAsync is acceptable here
        // because IFCMService only resolves lazily off FrontNotifierBackgroundService
        // (primary-only, off the request path) AFTER SecretsBootstrapService has already
        // patched the store. ISecretsStore.GetAsync is a single indexed Postgres row
        // read; the blocking call happens exactly once per process at DI-resolve time.
        services.AddSingleton<IFCMService>(sp =>
        {
            if (role != NodeGroup.Primary)
                return sp.GetRequiredService<NullFCMService>();

            var secrets = sp.GetRequiredService<ISecretsStore>();
            var serviceAccountJson = secrets
                .GetAsync("fcm:service_account_json", CancellationToken.None)
                .GetAwaiter().GetResult();

            if (string.IsNullOrWhiteSpace(serviceAccountJson))
            {
                sp.GetRequiredService<ILogger<FirebaseFCMService>>().LogInformation(
                    "[fcm] internal.secrets:fcm:service_account_json not seeded — using NullFCMService (push disabled).");
                return sp.GetRequiredService<NullFCMService>();
            }

            return sp.GetRequiredService<FirebaseFCMService>();
        });

        // Singleton task owner — primary owns tasks, auxiliary/sidecar do not.
        ISingletonTaskOwner taskOwner = role == NodeGroup.Primary
            ? new PrimaryOnlySingletonTaskOwner()
            : new NullSingletonTaskOwner();

        services.AddSingleton(taskOwner);

        // Background services gated on primary role.
        if (role == NodeGroup.Primary)
        {
            services.AddHostedService<FrontNotifierBackgroundService>();
        }

        return services;
    }

    /// <summary>
    /// Resolves the node role from environment variables and registers cluster coordination services.
    /// Reads FLY_PROCESS_GROUP, then OCTOCON_NODE_GROUP, then defaults to "auxiliary".
    /// </summary>
    public static IServiceCollection AddInterfoldCluster(this IServiceCollection services, IConfiguration config)
    {
        var opts = new ClusterConfiguration();
        ConfigurationServiceCollectionExtensions.ApplyCluster(opts, config);
        return services.AddInterfoldCluster(NodeGroupResolver.Resolve(opts.NodeGroup));
    }
}
