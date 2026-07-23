using Interfold.Domain.Friendships;

namespace Interfold.Friendships.Api.DependencyInjection;

/// <summary>Friendships feature module — the DI equivalent of the six friendship-handler
/// <c>AddSingleton</c> calls previously living in <see cref="Interfold.Infrastructure.DependencyInjection.ServiceCollectionExtensions.AddInterfoldDomainHandlers"/>.
/// Consumed once from <c>Interfold.Api.Host/Program.cs</c> alongside the other
/// module extensions.</summary>
public static class FriendshipsModuleServiceCollectionExtensions
{
    /// <summary>Registers everything the Friendships feature owns: the six friendship
    /// command-handler singletons. <c>IFriendshipRepository</c> is registered by the active
    /// persistence adapter (Scylla / Postgres / InMemory) via
    /// <c>AddInterfoldPersistence</c>, so no repository registration lives here.</summary>
    public static IServiceCollection AddFriendshipsModule(this IServiceCollection services) =>
        services
            .AddSingleton<RemoveFriendshipCommandHandler>()
            .AddSingleton<SetFriendTrustCommandHandler>()
            .AddSingleton<SendFriendRequestCommandHandler>()
            .AddSingleton<AcceptFriendRequestCommandHandler>()
            .AddSingleton<RejectFriendRequestCommandHandler>()
            .AddSingleton<CancelFriendRequestCommandHandler>();
}
