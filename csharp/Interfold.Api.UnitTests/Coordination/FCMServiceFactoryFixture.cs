using Interfold.Contracts.Enums;
using Interfold.Contracts.Secrets;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.DependencyInjection;
using Interfold.Infrastructure.InMemory;
using Interfold.Infrastructure.InMemory.Repository;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core.Interfaces;

namespace Interfold.Api.UnitTests.Coordination;

/// <summary>
/// TUnit fixture that composes the DI graph exercised by
/// <see cref="FCMServiceFactoryTests"/>. Each <see cref="CreateScope"/> call yields a
/// fresh, self-contained <see cref="ServiceProvider"/> because the
/// <see cref="Interfold.Domain.Abstractions.IFCMService"/> factory in
/// <see cref="ServiceCollectionExtensions.AddInterfoldCluster(IServiceCollection, NodeGroup)"/>
/// snapshots the secrets state at singleton-construction time — two scenarios that share
/// a container would poison each other's routing decisions.
/// </summary>
/// <remarks>
/// Registered on the test class via
/// <c>[ClassDataSource&lt;FCMServiceFactoryFixture&gt;(Shared = SharedType.PerTestSession)]</c>
/// and injected through the primary constructor, matching the fixture pattern used by
/// <c>Interfold.IntegrationTests</c>. The fixture itself holds no per-test state, so
/// sharing it across the whole session is safe and skips redundant TUnit lifecycle work.
/// </remarks>
public sealed class FCMServiceFactoryFixture : IAsyncInitializer
{
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Builds an isolated provider that mirrors the production DI graph for the given
    /// <paramref name="role"/> and secrets shape. Callers own the returned scope and
    /// must dispose it (typically <c>await using</c>) so the singleton
    /// <see cref="Interfold.Domain.Abstractions.IFCMService"/> instance is torn down before
    /// the next scenario starts.
    /// </summary>
    public FCMFactoryScope CreateScope(NodeGroup role, bool seedServiceAccount)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        // Wire the minimal InMemory-repo chain FirebaseFCMService needs, even in the
        // "not selected" tests — its concrete type is still registered, so DI must be
        // able to construct it if asked. The chain covers tokens → friendships,
        // account, and the alter repo's own dependencies (region, settings, polls).
        services.AddSingleton<IRegionContext>(_ => new InMemoryRegionContext());
        services.AddSingleton<IFriendshipRepository, InMemoryFriendshipRepository>();
        services.AddSingleton<INotificationTokenRepository, InMemoryNotificationTokenRepository>();
        services.AddSingleton<IPollRepository, InMemoryPollRepository>();
        services.AddSingleton<ISettingsFieldRepository, InMemorySettingsFieldRepository>();
        services.AddSingleton<IAccountRepository, InMemoryAccountRepository>();
        services.AddSingleton<IAlterRepository, InMemoryAlterRepository>();

        var store = new InMemorySecretsStore();
        if (seedServiceAccount)
        {
            store.Seed("fcm:service_account_json", "{\"type\":\"service_account\",\"project_id\":\"test\"}");
        }
        services.AddSingleton<ISecretsStore>(store);

        services.AddInterfoldCluster(role);
        return new FCMFactoryScope(services.BuildServiceProvider());
    }
}

/// <summary>
/// Owns the lifetime of a single scenario's <see cref="ServiceProvider"/>. Exposes the
/// provider as <see cref="IServiceProvider"/> so callers stay on the abstraction, and
/// routes disposal through <see cref="ServiceProvider.DisposeAsync"/> so any registered
/// <see cref="IAsyncDisposable"/> singleton is torn down cleanly and <see cref="IDisposable"/>
/// singletons (e.g. <see cref="Interfold.Infrastructure.Coordination.FirebaseFCMService"/>)
/// are handled by the same walker.
/// </summary>
public sealed class FCMFactoryScope(ServiceProvider provider) : IAsyncDisposable
{
    public IServiceProvider Services => provider;

    public ValueTask DisposeAsync() => provider.DisposeAsync();
}
