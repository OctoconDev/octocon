using Interfold.Shared.Contracts.Ids;

namespace Interfold.Shared.Contracts.Events;

public sealed record GlobalJournalEntryCreatedEvent(ScopedSystemId TargetSystemId, EntryId EntryId) : ITargetedClusterEvent;

public sealed record GlobalJournalEntryUpdatedEvent(ScopedSystemId TargetSystemId, EntryId EntryId) : ITargetedClusterEvent;

public sealed record GlobalJournalEntryDeletedEvent(ScopedSystemId TargetSystemId, EntryId EntryId) : ITargetedClusterEvent;

public sealed record AlterJournalEntryCreatedEvent(ScopedSystemId TargetSystemId, EntryId EntryId) : ITargetedClusterEvent;

public sealed record AlterJournalEntryUpdatedEvent(ScopedSystemId TargetSystemId, EntryId EntryId) : ITargetedClusterEvent;

public sealed record AlterJournalEntryDeletedEvent(ScopedSystemId TargetSystemId, EntryId EntryId) : ITargetedClusterEvent;
