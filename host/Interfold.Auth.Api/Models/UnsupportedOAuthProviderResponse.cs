using Interfold.Shared.Contracts;

namespace Interfold.Api.Models;

/// <summary>
/// 400 body for an unknown OAuth <c>{provider}</c> route value. Same
/// <c>{"error","code","provider"}</c> shape as the previous anonymous object — the
/// provider echoes the raw route string, which is exactly what failed to parse.
/// </summary>
public sealed record UnsupportedOAuthProviderResponse(string Error, ErrorCode Code, string Provider);
