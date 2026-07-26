using Interfold.Shared.Contracts.Ids;

namespace Interfold.Settings.Contracts.Models.Read;

/// <summary>
/// Response body for <c>GET /settings/link_token</c>. The token JSON-serializes as the raw
/// string (see <see cref="LinkToken"/>) so the client-visible shape is unchanged.
/// </summary>
public sealed record LinkTokenReadModel(LinkToken Token);
