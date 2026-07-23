using Interfold.Shared.Contracts.Enums;

namespace Interfold.Api.Models;

/// <summary>
/// Body of <c>GET /health/node-role</c>. Serializes as
/// <c>{"role":"primary","owns_singletons":true}</c> — the role via the enum's lowercase
/// wire converter (the previous anonymous object lower-cased <c>ToString()</c> by hand).
/// </summary>
public sealed record NodeRoleResponse(NodeGroup Role, bool OwnsSingletons);
