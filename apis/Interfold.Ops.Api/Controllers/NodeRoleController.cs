using Interfold.Ops.Api.Models;
using Interfold.Shared.Api.Controllers.Base;
using Interfold.Shared.Domain.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Interfold.Ops.Api.Controllers;

/// <summary>
/// Exposes node-role metadata for load-balancer and ops health checks.
/// </summary>
[AllowAnonymous]
[Route("health")]
public sealed class NodeRoleController : InterfoldControllerBase
{
    private readonly INodeRoleContext _nodeRole;

    public NodeRoleController(INodeRoleContext nodeRole)
    {
        _nodeRole = nodeRole;
    }

    /// <summary>
    /// Returns the current node's role and whether it owns singleton background tasks.
    /// <para>
    /// Equivalent to <c>Octocon.RPC.NodeTracker.current_group/0</c> from the legacy runtime.
    /// </para>
    /// </summary>
    [HttpGet("node-role")]
    public IActionResult GetNodeRole()
    {
        return Ok(new NodeRoleResponse(_nodeRole.Role, _nodeRole.IsPrimary));
    }
}
