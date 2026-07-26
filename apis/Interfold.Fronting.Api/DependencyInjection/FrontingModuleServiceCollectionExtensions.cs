using Interfold.Fronting.Domain;

namespace Interfold.Fronting.Api.DependencyInjection;

/// <summary>Fronting feature module — the DI equivalent of the seven fronting-handler
/// <c>AddSingleton</c> calls previously living in <see cref="Interfold.Infrastructure.DependencyInjection.ServiceCollectionExtensions.AddInterfoldDomainHandlers"/>.
/// Consumed once from <c>Interfold.Api.Host/Program.cs</c> alongside the other
/// module extensions.</summary>
public static class FrontingModuleServiceCollectionExtensions
{
    /// <summary>Registers everything the Fronting feature owns: the seven fronting
    /// command-handler singletons. <c>IFrontingRepository</c> is registered by the active
    /// persistence adapter (Scylla / Postgres / InMemory) via
    /// <c>AddInterfoldPersistence</c>, so no repository registration lives here.</summary>
    public static IServiceCollection AddFrontingModule(this IServiceCollection services) =>
        services
            .AddSingleton<StartFrontCommandHandler>()
            .AddSingleton<EndFrontCommandHandler>()
            .AddSingleton<BulkUpdateFrontCommandHandler>()
            .AddSingleton<SetFrontCommandHandler>()
            .AddSingleton<SetPrimaryFrontCommandHandler>()
            .AddSingleton<DeleteFrontByIdCommandHandler>()
            .AddSingleton<UpdateFrontCommentCommandHandler>();
}
