namespace Interfold.Settings.Contracts.Models.Wire;

/// <summary>
/// 503 body for <c>GET /settings/link_token</c> on non-primary nodes. Intentionally
/// non-standard (carries <c>hint</c> instead of <c>code</c>) — the Kotlin client and the
/// legacy Elixir server both use this exact shape, so it is frozen.
/// </summary>
public sealed record LinkTokenUnavailableResponse(string Error, string Hint);
