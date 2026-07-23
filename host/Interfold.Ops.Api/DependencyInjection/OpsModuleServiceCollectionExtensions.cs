namespace Interfold.Ops.Api.DependencyInjection;

/// <summary>Ops feature module — the facade shape has no owned handlers or
/// abstractions to register (see the deliberate absence of an <c>Interfold.Ops.Domain</c>
/// project and an <c>Interfold.Ops.Contracts</c> project — Ops has nothing to share
/// cross-module). <c>AddOpsModule</c> exists purely for wiring symmetry with the other
/// feature modules so <c>Interfold.Api.Host/Program.cs</c> reads uniformly. Consumed
/// once from the module-registration block alongside the other
/// <c>Add&lt;Feature&gt;Module</c> extensions.</summary>
public static class OpsModuleServiceCollectionExtensions
{
    /// <summary>No-op today. <c>NodeRoleController</c> binds <c>INodeRoleContext</c>
    /// (registered by <c>AddInterfoldCluster</c>); <c>TrustController</c> binds
    /// <c>IOptions&lt;TrustOptions&gt;</c> (registered by <c>AddInterfoldOptions</c>);
    /// nothing here to register.</summary>
    public static IServiceCollection AddOpsModule(this IServiceCollection services) => services;
}
