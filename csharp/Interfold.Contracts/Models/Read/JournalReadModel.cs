using Interfold.Contracts.Ids;
using Interfold.Contracts.Validation;

namespace Interfold.Contracts.Models.Read;

public sealed record JournalReadModel(
    EntryId Id,
    SystemId UserId,
    string Title,
    string? Content,
    HexColor? Color,
    bool Locked,
    bool Pinned,
    DateTime InsertedAt,
    DateTime UpdatedAt,
    IReadOnlyList<AlterId> Alters
);


public sealed record CreateGlobalJournalRequest(
    string Title,
    EntityVersion? ExpectedVersion = null
);

public sealed record UpdateGlobalJournalRequest(
    string? Title = null,
    string? Content = null,
    HexColor? Color = null,
    EntityVersion? ExpectedVersion = null
);

public sealed record DeleteGlobalJournalRequest(
    EntityVersion? ExpectedVersion = null
);

public sealed record JournalActionRequest(
    EntityVersion? ExpectedVersion = null
);

// ValidAlterId lives on the constructor parameter (default target), NOT the property:
// MVC's ObjectModelValidator throws InvalidOperationException on records that carry
// validation metadata on properties (see ThrowIfRecordTypeHasValidationOnProperties)
// — the parameter target is where MVC's parameter-binding validator actually reads
// the attribute from.
public sealed record JournalAlterRequest(
    [ValidAlterId] AlterId AlterId,
    EntityVersion? ExpectedVersion = null
);
