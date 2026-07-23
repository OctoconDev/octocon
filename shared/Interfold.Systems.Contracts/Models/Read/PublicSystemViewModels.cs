using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;

namespace Interfold.Contracts.Models.Read;

/// <summary>Read model for <c>GET /api/systems/{systemId}</c>. Emits
/// <c>{ id, avatar_url, avatar_source, username, description }</c> under snake_case.</summary>
public sealed record PublicSystemReadModel(
    SystemId Id,
    AvatarUrl? AvatarUrl,
    AvatarSource? AvatarSource,
    Username? Username,
    string? Description
) : IAvatarBearing;
