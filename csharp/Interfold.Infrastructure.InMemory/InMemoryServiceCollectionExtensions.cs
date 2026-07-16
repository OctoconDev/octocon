using Interfold.Contracts;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Secrets;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.DependencyInjection;
using Interfold.Infrastructure.InMemory.Repository;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.InMemory;

public static class InMemoryServiceCollectionExtensions
{
    // Lazy<T> (default ExecutionAndPublication mode) is a wait-for-completion gate: exactly
    // one caller runs the factory while every other caller blocks until it returns. The
    // previous Interlocked.Exchange gate was fire-and-forget — the second caller would
    // observe `_added == 1` and return *before* the first caller had actually populated
    // PersistenceReg, then race ahead to AddInterfoldPersistence and trip the
    // "Persistence mode has not yet been implemented" throw. The integration suite hits
    // this when multiple WebApplicationFactory<Program> instances build their hosts in
    // parallel; replacing the gate with Lazy<bool> serialises observers behind the
    // registration so PersistenceReg is guaranteed populated by the time Register() returns.
    private static readonly Lazy<bool> Registration = new(() =>
    {
        ServiceCollectionExtensions.AddPersistenceMode(PersistenceMode.InMemory, AddInMemoryPersistence);
        return true;
    });

    public static void Register() => _ = Registration.Value;

    private static IServiceCollection AddInMemoryPersistence(
        IServiceCollection services,
        PersistenceConfiguration options)
    {
        return services
            .AddSingleton<IRegionContext>(_ => new InMemoryRegionContext(
                options.ScyllaKeyspace))
            // Seed the in-memory secrets store from the `OCTOCON_INMEMORY_SECRETS_SEED__*`
            // env-var family so an external runner (Kotlin Testcontainers harness, ad-hoc
            // local container, etc.) can bootstrap the published image without an in-process
            // hook. The `__`-to-`:` remap performed by EnvironmentVariablesConfigurationProvider
            // means the bound config keys are `OCTOCON_INMEMORY_SECRETS_SEED:*`; both env-var
            // and FactoryConfigurationProvider overrides land on the same InMemorySecretsSeedOptions
            // instance, so tests do not need to mutate global env state. Blank/missing values
            // are skipped silently — SecretsBootstrapService is the single source of fail-fast
            // for the mandatory `encryption:pepper` row and we deliberately don't duplicate
            // that contract here.
            .AddSingleton<ISecretsStore>(sp =>
            {
                var seed = sp.GetRequiredService<IOptions<InMemorySecretsSeedOptions>>().Value;
                var store = new InMemorySecretsStore();
                Seed(store, SecretsStoreKeys.EncryptionPepper,        seed.EncryptionPepper);
                Seed(store, SecretsStoreKeys.AuthJwtEs256PrivatePem,  seed.AuthJwtEs256PrivatePem);
                Seed(store, SecretsStoreKeys.AuthDeepLinkSecret,      seed.AuthDeepLinkSecret);
                Seed(store, SecretsStoreKeys.AuthJwtRsa256PrivatePem, seed.AuthJwtRsa256PrivatePem);
                return store;
            })
            .AddSingleton<INotificationTokenRepository, InMemoryNotificationTokenRepository>()
            .AddSingleton<IEncryptionStateRepository, InMemoryEncryptionStateRepository>()
            .AddSingleton<IAccountRepository, InMemoryAccountRepository>()
            .AddSingleton<ISettingsFieldRepository, InMemorySettingsFieldRepository>()
            .AddSingleton<IPollRepository, InMemoryPollRepository>()
            .AddSingleton<IAlterRepository, InMemoryAlterRepository>()
            .AddSingleton<IFrontingRepository, InMemoryFrontingRepository>()
            .AddSingleton<IFriendshipRepository, InMemoryFriendshipRepository>()
            .AddSingleton<ITagRepository, InMemoryTagRepository>()
            .AddSingleton<IJournalRepository, InMemoryJournalRepository>()
            .AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>()
            .AddSingleton<IImportOperationRepository, InMemoryImportOperationRepository>()
            .AddSingleton<IAuthTokenRevocationRepository, InMemoryAuthTokenRevocationRepository>();
    }

    private static void Seed(InMemorySecretsStore store, SecretsStoreKey secretKey, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            store.Seed(secretKey, value);
    }
}