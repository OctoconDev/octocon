using Interfold.Contracts;
using Interfold.Contracts.Enums;

namespace Interfold.Api.Models;

/// <summary>
/// Body of <c>GET /health/node-role</c>. Serializes as
/// <c>{"role":"primary","owns_singletons":true}</c> — the role via the enum's lowercase
/// wire converter (the previous anonymous object lower-cased <c>ToString()</c> by hand).
/// </summary>
public sealed record NodeRoleResponse(NodeGroup Role, bool OwnsSingletons);

/// <summary>
/// 400 body for an unknown OAuth <c>{provider}</c> route value. Same
/// <c>{"error","code","provider"}</c> shape as the previous anonymous object — the
/// provider echoes the raw route string, which is exactly what failed to parse.
/// </summary>
public sealed record UnsupportedOAuthProviderResponse(string Error, ErrorCode Code, string Provider);

/// <summary>
/// 503 body for <c>GET /settings/link_token</c> on non-primary nodes. Intentionally
/// non-standard (carries <c>hint</c> instead of <c>code</c>) — the Kotlin client and the
/// legacy Elixir server both use this exact shape, so it is frozen.
/// </summary>
public sealed record LinkTokenUnavailableResponse(string Error, string Hint);
