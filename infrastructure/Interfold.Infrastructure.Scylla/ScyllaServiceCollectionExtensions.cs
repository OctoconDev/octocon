using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.DependencyInjection;
using Interfold.Infrastructure.Scylla.Fixups;
using Interfold.Infrastructure.Scylla.Repository;
using Microsoft.Extensions.DependencyInjection;

namespace Interfold.Infrastructure.Scylla;

public static class ScyllaServiceCollectionExtensions
{
    private static readonly Action Registration = PersistenceRegistration.Create(PersistenceMode.ScyllaPostgres, AddScyllaPersistence);
    public static void Register() => Registration();
    
    private static IServiceCollection AddScyllaPersistence(
        IServiceCollection services,
        PersistenceConfiguration options)
    {
        var pipeline = services
                .AddSingleton<IScyllaConfigResolver, ScyllaConfigResolver>()
                .AddSingleton<IScyllaSessionProvider, ScyllaSessionProvider>()
                .AddSingleton<IScyllaKeyspaceResolver, ScyllaKeyspaceResolver>()
                .AddSingleton<IScyllaScopeResolver, ScyllaScopeResolver>()
                .AddSingleton<IRegionContext, ScyllaUserRegistryRegionContext>()
                .AddSingleton<IAccountRepository, ScyllaAccountRepository>()
                .AddSingleton<INotificationTokenRepository, ScyllaNotificationTokenRepository>()
                .AddSingleton<IEncryptionStateRepository, ScyllaEncryptionStateRepository>()
                .AddSingleton<ISettingsFieldRepository, ScyllaSettingsFieldRepository>()
                .AddSingleton<IAlterRepository, ScyllaAlterRepository>()
                .AddSingleton<IFrontingRepository, ScyllaFrontingRepository>()
                .AddSingleton<IFriendshipRepository, ScyllaFriendshipRepository>()
                .AddSingleton<ITagRepository, ScyllaTagRepository>()
                .AddSingleton<IJournalRepository, ScyllaJournalRepository>()
                .AddSingleton<IPollRepository, ScyllaPollRepository>()
                .AddSingleton<IImportOperationRepository, ScyllaImportOperationRepository>()
                .AddHostedService<ScyllaMigrationService>()
                // Ordering matters: migrations first (grants + column additions), then fixup
                // uses the app-user session to normalise legacy color rows.
                .AddHostedService<HexColorFixupService>();

        return pipeline;
    }
}