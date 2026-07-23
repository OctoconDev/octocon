using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;

namespace Interfold.Shared.Contracts.Models.Read;

public sealed record CreateAlterRequest(
    string Name
);

public sealed record UpdateAlterRequest(
    string? Name = null,
    string? Description = null,
    AvatarUrl? AvatarUrl = null,
    HexColor? Color = null,
    string? Pronouns = null,
    VisibilityLevel? SecurityLevel = null,
    string? ProxyName = null,
    string? Alias = null,
    bool? Untracked = null,
    bool? Archived = null,
    bool? Pinned = null,
    IReadOnlyList<UpdateAlterFieldRequest>? Fields = null
);

public sealed record UpdateAlterFieldRequest(
    FieldId Id,
    string? Value
);
