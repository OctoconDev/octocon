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
    // See InMemoryServiceCollectionExtensions.Registration for the full rationale: the
    // previous Interlocked.Exchange gate let a second caller observe the "already
    // registered" flag before the first caller had populated PersistenceReg, which surfaced
    // as a "Persistence mode has not yet been implemented" throw under parallel
    // WebApplicationFactory<Program> host builds. Lazy<bool> (ExecutionAndPublication mode)
    // serialises observers behind the registration call so every Register() return
    // guarantees PersistenceReg[ScyllaPostgres] contains this adapter.
    private static readonly Lazy<bool> Registration = new(() =>
    {
        ServiceCollectionExtensions.AddPersistenceMode(PersistenceMode.ScyllaPostgres, AddScyllaPersistence);
        return true;
    });

    public static void Register() => _ = Registration.Value;
    
    private static IServiceCollection AddScyllaPersistence(
        IServiceCollection services,
        PersistenceConfiguration options)
    {
        var pipeline = services
                .AddSingleton<IScyllaConfigResolver, ScyllaConfigResolver>()
                .AddSingleton<IScyllaSessionProvider, ScyllaSessionProvider>()
                .AddSingleton<IScyllaKeyspaceResolver, ScyllaKeyspaceResolver>()
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