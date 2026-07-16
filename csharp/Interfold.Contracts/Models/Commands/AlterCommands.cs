using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;

namespace Interfold.Contracts.Models.Commands;

public sealed record CreateAlterCommand(string Name, DateTimeOffset CreatedAt);

public sealed record UpdateAlterCommand(
    AlterId AlterId,
    string? Name,
    string? Description,
    AvatarUrl? AvatarUrl,
    AvatarSource? AvatarSource,
    HexColor? Color,
    string? Pronouns,
    VisibilityLevel? SecurityLevel,
    IReadOnlyList<AlterFieldCommand>? Fields,
    string? ProxyName,
    string? Alias,
    bool? Untracked,
    bool? Archived,
    bool? Pinned,
    DateTimeOffset UpdatedAt,
    bool ClearAvatar = false
);

public sealed record DeleteAlterCommand(AlterId AlterId);

// Id references the settings field definition (FieldId). Property name is frozen:
// it serializes as "id" in persisted command JSON and idempotency hashes.
public sealed record AlterFieldCommand(FieldId Id, string? Value);
