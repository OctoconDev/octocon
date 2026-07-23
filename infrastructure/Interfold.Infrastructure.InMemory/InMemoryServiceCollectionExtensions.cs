using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Secrets;
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
            // Seeded from OCTOCON_INMEMORY_SECRETS_SEED__* so external runners can bootstrap
            // the published image without an in-process hook. Blanks are skipped silently —
            // SecretsBootstrapService is the single fail-fast for mandatory rows.
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