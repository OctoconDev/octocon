using Interfold.Contracts.Ids;
using Interfold.Contracts.Models;

namespace Interfold.Contracts.Models.Read;

public sealed record AlterJournalReadModel(
    EntryId Id,
    SystemId UserId,
    AlterId AlterId,
    string Title,
    string? Content,
    HexColor? Color,
    bool Locked,
    bool Pinned,
    DateTime InsertedAt,
    DateTime UpdatedAt
);

public sealed record CreateAlterJournalRequest(
    string Title
);

public sealed record UpdateAlterJournalRequest(
    string? Title = null,
    string? Content = null,
    HexColor? Color = null
);

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


public sealed record AlterJournalRef(EntryId EntryId, AlterId AlterId);