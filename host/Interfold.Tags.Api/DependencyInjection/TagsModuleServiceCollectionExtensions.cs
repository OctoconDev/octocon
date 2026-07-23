using Interfold.Shared.Domain.Tags;

namespace Interfold.Tags.Api.DependencyInjection;

/// <summary>Tags feature module — the DI equivalent of the seven tag-handler
/// <c>AddSingleton</c> calls previously living in <see cref="Interfold.Infrastructure.DependencyInjection.ServiceCollectionExtensions.AddInterfoldDomainHandlers"/>.
/// Consumed once from <c>Interfold.Api.Host/Program.cs</c> alongside the other
/// module extensions.</summary>
public static class TagsModuleServiceCollectionExtensions
{
    /// <summary>Registers everything the Tags feature owns: the seven tag command-handler
    /// singletons. <c>ITagRepository</c> is registered by the active persistence adapter
    /// (Scylla / Postgres / InMemory) via <c>AddInterfoldPersistence</c>, so no repository
    /// registration lives here.</summary>
    public static IServiceCollection AddTagsModule(this IServiceCollection services) =>
        services
            .AddSingleton<CreateTagCommandHandler>()
            .AddSingleton<UpdateTagCommandHandler>()
            .AddSingleton<DeleteTagCommandHandler>()
            .AddSingleton<AttachAlterToTagCommandHandler>()
            .AddSingleton<DetachAlterFromTagCommandHandler>()
            .AddSingleton<SetParentTagCommandHandler>()
            .AddSingleton<RemoveParentTagCommandHandler>();
}
