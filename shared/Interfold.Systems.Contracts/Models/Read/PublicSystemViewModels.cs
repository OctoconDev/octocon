using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;

namespace Interfold.Systems.Contracts.Models.Read;

/// <summary>Read model for <c>GET /api/systems/{systemId}</c>. Emits
/// <c>{ id, avatar_url, avatar_source, username, description }</c> under snake_case.</summary>
public sealed record PublicSystemReadModel(
    SystemId Id,
    AvatarUrl? AvatarUrl,
    AvatarSource? AvatarSource,
    Username? Username,
    string? Description
) : IAvatarBearing;
