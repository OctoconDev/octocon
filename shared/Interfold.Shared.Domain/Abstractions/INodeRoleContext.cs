using Interfold.Shared.Contracts.Enums;

namespace Interfold.Shared.Domain.Abstractions;

/// <summary>Current-node role, resolved once at startup from FLY_PROCESS_GROUP /
/// OCTOCON_NODE_GROUP.</summary>
public interface INodeRoleContext
{
    NodeGroup Role { get; }

    /// <summary>Primary — owns singletons, runs background jobs.</summary>
    bool IsPrimary => Role == NodeGroup.Primary;

    /// <summary>Auxiliary — HTTP-only.</summary>
    bool IsAuxiliary => Role == NodeGroup.Auxiliary;

    /// <summary>Sidecar — CPU-task node.</summary>
    bool IsSidecar => Role == NodeGroup.Sidecar;
}
