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
    private static readonly Action Registration = PersistenceRegistration.Create(PersistenceMode.InMemory, AddInMemoryPersistence);
    public static void Register() => Registration();

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