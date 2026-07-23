using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.ImportJobs;
using Interfold.Infrastructure.Coordination;

namespace Interfold.Infrastructure.DependencyInjection;

public static partial class ServiceCollectionExtensions
{
    /// <summary>Registers node-role and cluster coordination services. Call after
    /// <see cref="AddInterfoldPersistence"/> in <c>Program.cs</c>.</summary>
    public static IServiceCollection AddInterfoldCluster(
        this IServiceCollection services,
        NodeGroup role)
    {
        services.AddSingleton<INodeRoleContext>(new NodeRoleContext(role));

        services.AddSingleton<IClusterEventBus, InProcessEventBus>();

        // Per-system mutex lives in IImportOperationRepository (LWT on
        // active_import_by_system), so one channel + one worker is sufficient.
        services.AddSingleton<IImportJobQueue, InProcessImportJobQueue>();

        // Both implementations are registered concretely so the factory below can pick.
        services.AddSingleton<NullFCMService>();
        services.AddSingleton<FirebaseFCMService>();

        // Primary + seeded fcm:service_account_json → real sender; otherwise no-op.
        services.AddSingleton<IFCMService>(sp =>
        {
            if (role != NodeGroup.Primary)
                return sp.GetRequiredService<NullFCMService>();

            var serviceAccountJson = sp.GetRequiredService<IOptions<FcmConfiguration>>().Value.ServiceAccountJson;

            if (string.IsNullOrWhiteSpace(serviceAccountJson))
            {
                sp.GetRequiredService<ILogger<FirebaseFCMService>>().LogInformation(
                    "[fcm] internal.secrets:{Key} not seeded — using NullFCMService (push disabled).",
                    SecretsStoreKeys.FcmServiceAccountJson);
                return sp.GetRequiredService<NullFCMService>();
            }

            return sp.GetRequiredService<FirebaseFCMService>();
        });

        ISingletonTaskOwner taskOwner = role == NodeGroup.Primary
            ? new PrimaryOnlySingletonTaskOwner()
            : new NullSingletonTaskOwner();

        services.AddSingleton(taskOwner);

        if (role == NodeGroup.Primary)
        {
            services.AddHostedService<FrontNotifierBackgroundService>();
        }

        return services;
    }
}
