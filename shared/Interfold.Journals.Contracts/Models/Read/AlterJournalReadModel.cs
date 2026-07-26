using Interfold.Journals.Contracts.Ids;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Journals.Contracts.Models.Read;

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

public sealed record AlterJournalRef(EntryId EntryId, AlterId AlterId);
