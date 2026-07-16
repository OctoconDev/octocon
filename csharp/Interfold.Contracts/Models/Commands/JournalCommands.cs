using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Models.Commands;

public sealed record CreateGlobalJournalEntryCommand(string Title);

public sealed record UpdateGlobalJournalEntryCommand(
    EntryId EntryId,
    string? Title,
    string? Content,
    HexColor? Color
);

public sealed record DeleteGlobalJournalEntryCommand(EntryId EntryId);

public sealed record SetGlobalJournalLockedCommand(EntryId EntryId, bool Locked);

public sealed record SetGlobalJournalPinnedCommand(EntryId EntryId, bool Pinned);

public sealed record AttachAlterToGlobalJournalCommand(EntryId EntryId, AlterId AlterId);

public sealed record DetachAlterFromGlobalJournalCommand(EntryId EntryId, AlterId AlterId);

public sealed record CreateAlterJournalEntryCommand(
    AlterId AlterId,
    string Title,
    DateTimeOffset CreatedAt
);

public sealed record UpdateAlterJournalEntryCommand(
    EntryId EntryId,
    string? Title,
    string? Content,
    HexColor? Color,
    DateTimeOffset UpdatedAt
);

public sealed record DeleteAlterJournalEntryCommand(EntryId EntryId);

public sealed record SetAlterJournalLockedCommand(EntryId EntryId, bool Locked);

public sealed record SetAlterJournalPinnedCommand(EntryId EntryId, bool Pinned);
