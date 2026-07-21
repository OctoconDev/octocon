using Interfold.Contracts;
using Interfold.Contracts.Configuration;
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
                // Registered after ScyllaMigrationService so IHostedLifecycleService.StartingAsync
                // runs in that order: schema migrations first (which grant the app user MODIFY on
                // every regional keyspace), then the fixup uses the app-user session to normalise
                // legacy color rows to the shape strict HexColor.FromNullable now requires.
                //
                // Note: the primary_front int -> primary_front_alter smallint narrowing lives
                // inline in ScyllaMigrationService.StartingAsync (migration 006 + one backfill),
                // not as a separate hosted service — the ADD and the copy have to run in the
                // same startup pass before repositories bind to the new column, and
                // hosted-service ordering can't guarantee that.
                .AddHostedService<HexColorFixupService>();

        return pipeline;
    }
}