using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Validation;

namespace Interfold.Shared.Contracts.Models.Read;

public sealed record TagReadModel(
    TagId Id,
    string Name,
    HexColor? Color,
    string? Description,
    TagId? ParentTagId,
    IReadOnlyList<AlterId> Alters,
    DateTime InsertedAt,
    DateTime UpdatedAt,
    VisibilityLevel SecurityLevel,
    SystemId? UserId
);

public sealed record TagPublicReadModel(
    TagId Id,
    string Name,
    HexColor? Color,
    string? Description,
    TagId? ParentTagId,
    IReadOnlyList<BareAlter> Alters,
    DateTime InsertedAt,
    DateTime UpdatedAt,
    VisibilityLevel SecurityLevel,
    SystemId? UserId
);

public sealed record CreateTagRequest(
    string Name,
    TagId? ParentTagId
);

public sealed record UpdateTagRequest(
    string? Name = null,
    HexColor? Color = null,
    string? Description = null,
    VisibilityLevel? SecurityLevel = null
);

// ValidAlterId lives on the constructor parameter, NOT the property — see the same
// pattern in JournalAlterRequest / FrontStartRequest. MVC's ObjectModelValidator
// rejects records that carry validation metadata on their auto-generated properties
// (ThrowIfRecordTypeHasValidationOnProperties) and reads parameter-level attributes
// via its parameter-binding validator instead.
public sealed record TagAlterRequest(
    [ValidAlterId] AlterId AlterId
);

public sealed record SetParentRequest(
    TagId? ParentTagId
);
