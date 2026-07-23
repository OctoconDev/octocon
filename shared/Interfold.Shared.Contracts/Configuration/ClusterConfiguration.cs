using Interfold.Shared.Contracts.Enums;

namespace Interfold.Shared.Contracts.Configuration;

/// <summary>
/// Cluster and deployment configuration for node role and orchestration.
/// Binds from environment variables:
///   - FLY_PROCESS_GROUP (fly.io automatic)
///   - OCTOCON_NODE_GROUP (manual override, takes precedence)
/// </summary>
public sealed class ClusterConfiguration
{
    public const string SectionName = "Cluster";

    /// <summary>
    /// Resolved node group. Bound from <c>FLY_PROCESS_GROUP</c> or <c>OCTOCON_NODE_GROUP</c> via
    /// <see cref="EnumWireExtensions.ParseNodeGroup"/>; wire values are <c>"primary"</c>,
    /// <c>"auxiliary"</c>, <c>"sidecar"</c>.
    /// </summary>
    public NodeGroup NodeGroup { get; set; } = NodeGroup.Auxiliary;
}
