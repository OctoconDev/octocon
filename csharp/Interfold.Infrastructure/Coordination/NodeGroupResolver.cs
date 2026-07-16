using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;

namespace Interfold.Infrastructure.Coordination;

/// <summary>
/// Thin adapter over <see cref="ClusterConfiguration.NodeGroup"/> and
/// <see cref="EnumWireExtensions.ParseNodeGroup"/>. Kept as a named entry point so callers
/// unable to import <c>Interfold.Contracts.Enums</c> directly still have a stable resolver name.
/// </summary>
/// <remarks>
/// Detection order mirrors the legacy Elixir runtime — <c>FLY_PROCESS_GROUP</c> then
/// <c>OCTOCON_NODE_GROUP</c> — but that layering now lives inside <c>ApplyCluster</c> so the
/// <see cref="ClusterConfiguration.NodeGroup"/> reaching this resolver is already the winning value.
/// </remarks>
public static class NodeGroupResolver
{
    /// <summary>Returns the already-bound node group off <see cref="ClusterConfiguration"/>.</summary>
    public static NodeGroup Resolve(ClusterConfiguration configuration) => configuration.NodeGroup;

    /// <summary>Parses a raw env-string value; delegates to <see cref="EnumWireExtensions.ParseNodeGroup"/>.</summary>
    public static NodeGroup Resolve(string? rawValue) => EnumWireExtensions.ParseNodeGroup(rawValue);
}
