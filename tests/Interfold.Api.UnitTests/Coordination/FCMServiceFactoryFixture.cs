using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
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

// TUnit fixture for FCMServiceFactoryTests. Each CreateScope yields a fresh SP because
// the IFCMService factory snapshots secrets at singleton-construction time — sharing
// containers across scenarios would cross-contaminate routing decisions.
public sealed class FCMServiceFactoryFixture : IAsyncInitializer
{
    public Task InitializeAsync() => Task.CompletedTask;

    public FCMFactoryScope CreateScope(NodeGroup role, bool seedServiceAccount)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        // FirebaseFCMService's concrete type is DI-resolvable even in "not selected"
        // scenarios; wire its transitive InMemory-repo chain so construction never
        // throws unexpectedly.
        services.AddSingleton<IRegionContext>(_ => new InMemoryRegionContext());
        services.AddSingleton<IFriendshipRepository, InMemoryFriendshipRepository>();
        services.AddSingleton<INotificationTokenRepository, InMemoryNotificationTokenRepository>();
        services.AddSingleton<IPollRepository, InMemoryPollRepository>();
        services.AddSingleton<ISettingsFieldRepository, InMemorySettingsFieldRepository>();
        services.AddSingleton<IAccountRepository, InMemoryAccountRepository>();
        services.AddSingleton<IAlterRepository, InMemoryAlterRepository>();

        services.Configure<FcmConfiguration>(o =>
            o.ServiceAccountJson = seedServiceAccount 
                ? "{\"type\":\"service_account\",\"project_id\":\"test\"}" 
                : null);

        services.AddInterfoldCluster(role);
        return new FCMFactoryScope(services.BuildServiceProvider());
    }
}

// Owns a single scenario's ServiceProvider; async disposal routes IDisposable and
// IAsyncDisposable singletons through the same walker.
public sealed class FCMFactoryScope(ServiceProvider provider) : IAsyncDisposable
{
    public IServiceProvider Services => provider;

    public ValueTask DisposeAsync() => provider.DisposeAsync();
}
