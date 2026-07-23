using Interfold.Contracts.Enums;
using Interfold.Domain.Abstractions;

namespace Interfold.Infrastructure.Coordination;

/// <summary>Stateless <see cref="INodeRoleContext"/> — role resolved once at startup.</summary>
public sealed class NodeRoleContext(NodeGroup role) : INodeRoleContext
{
    public NodeGroup Role { get; } = role;
}
