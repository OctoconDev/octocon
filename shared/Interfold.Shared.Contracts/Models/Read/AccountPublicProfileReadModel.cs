using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Contracts.Models.Read;

public sealed record AccountPublicProfileReadModel(
    SystemId SystemId,
    Username? Username,
    string? Description,
    AvatarUrl? AvatarUrl,
    AvatarSource? AvatarSource,
    DiscordId? DiscordId,
    Email? Email,
    AppleId? AppleId
) : IAvatarBearing;

public enum AccountLinkResult
{
    Success,
    AlreadyLinked,
    UserExists,
    UserNotFound
}
