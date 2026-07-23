using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Models.Read;

namespace Interfold.Shared.Domain.Abstractions.Repository;

public interface IJournalRepository
{
    Task<EntryId?> CreateGlobalAsync(SystemId systemId, CreateGlobalJournalEntryCommand command, CancellationToken cancellationToken = default);

    Task<bool> ExistsGlobalAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default);

    Task<bool> UpdateGlobalAsync(SystemId systemId, UpdateGlobalJournalEntryCommand command, CancellationToken cancellationToken = default);

    Task<bool> DeleteGlobalAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default);

    Task<bool> SetGlobalLockedAsync(SystemId systemId, EntryId entryId, bool locked, CancellationToken cancellationToken = default);

    Task<bool> SetGlobalPinnedAsync(SystemId systemId, EntryId entryId, bool pinned, CancellationToken cancellationToken = default);

    Task<bool> AttachGlobalAlterAsync(SystemId systemId, EntryId entryId, AlterId alterId, CancellationToken cancellationToken = default);

    Task<bool> DetachGlobalAlterAsync(SystemId systemId, EntryId entryId, AlterId alterId, CancellationToken cancellationToken = default);

    Task<EntryId?> CreateAlterAsync(SystemId systemId, CreateAlterJournalEntryCommand command, CancellationToken cancellationToken = default);

    Task<AlterJournalRef?> GetAlterRefAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default);

    Task<bool> UpdateAlterAsync(SystemId systemId, UpdateAlterJournalEntryCommand command, CancellationToken cancellationToken = default);

    Task<bool> DeleteAlterAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cascade cleanup for <see cref="Interfold.Shared.Domain.Alters.DeleteAlterCommandHandler"/>:
    /// wipes every per-alter journal entry the alter owned (both view tables in the Scylla
    /// repository) and detaches the alter from any global journals it was attached to.
    /// <para>
    /// Global journal rows themselves are left intact - they may be attached to other
    /// alters, and we do not want deleting one alter to silently delete a shared group
    /// journal. The alter-id rows in the <c>global_journal_alters</c> join table are
    /// removed so the deleted alter does not appear on any global journal's attached-alters
    /// list after this returns.
    /// </para>
    /// <para>
    /// Idempotent: returns gracefully when the alter has no journal entries and no global
    /// journal attachments. Returns the number of per-alter journal entries that were
    /// removed (the global-journal detach count is logged at the repository level but not
    /// surfaced; the caller only needs the alter-journal count for telemetry).
    /// </para>
    /// </summary>
    Task<int> DeleteAllForAlterAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default);

    Task<bool> SetAlterLockedAsync(SystemId systemId, EntryId entryId, bool locked, CancellationToken cancellationToken = default);

    Task<bool> SetAlterPinnedAsync(SystemId systemId, EntryId entryId, bool pinned, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AlterJournalReadModel>> ListAlterAsync(SystemId systemId, AlterId alterId, CancellationToken cancellationToken = default);

    Task<AlterJournalReadModel?> GetAlterAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<JournalReadModel>> ListGlobalAsync(SystemId systemId, CancellationToken cancellationToken = default);

    Task<JournalReadModel?> GetGlobalAsync(SystemId systemId, EntryId entryId, CancellationToken cancellationToken = default);
}