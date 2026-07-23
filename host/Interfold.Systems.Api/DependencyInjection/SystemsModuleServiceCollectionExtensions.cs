namespace Interfold.Systems.Api.DependencyInjection;

/// <summary>Systems feature module — the facade shape has no owned handlers or
/// abstractions to register (see the <c>Interfold.Systems.Domain</c> project's
/// deliberate absence). <c>AddSystemsModule</c> exists purely for wiring symmetry
/// with the other feature modules so <c>Interfold.Api.Host/Program.cs</c> reads
/// uniformly. Consumed once from the module-registration block alongside the
/// other <c>Add&lt;Feature&gt;Module</c> extensions.</summary>
public static class SystemsModuleServiceCollectionExtensions
{
    /// <summary>No-op today. <c>PublicSystemsController</c> consumes five repository
    /// interfaces (<c>IAccountRepository</c>, <c>IAlterRepository</c>,
    /// <c>ITagRepository</c>, <c>IFrontingRepository</c>, <c>IFriendshipRepository</c>)
    /// registered by the active persistence adapter via <c>AddInterfoldPersistence</c>;
    /// nothing here to register.</summary>
    public static IServiceCollection AddSystemsModule(this IServiceCollection services) => services;
}
