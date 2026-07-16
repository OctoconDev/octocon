using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Events;

public sealed record AlterCreatedEvent(ScopedSystemId TargetSystemId, AlterId AlterId) : ITargetedClusterEvent;

public sealed record AlterUpdatedEvent(ScopedSystemId TargetSystemId, AlterId AlterId) : ITargetedClusterEvent;

public sealed record AlterDeletedEvent(ScopedSystemId TargetSystemId, AlterId AlterId) : ITargetedClusterEvent;

public sealed record TagCreatedEvent(ScopedSystemId TargetSystemId, TagId TagId) : ITargetedClusterEvent;

public sealed record TagUpdatedEvent(ScopedSystemId TargetSystemId, TagId TagId) : ITargetedClusterEvent;

public sealed record TagDeletedEvent(ScopedSystemId TargetSystemId, TagId TagId) : ITargetedClusterEvent;

public sealed record SettingsFieldsChangedEvent(ScopedSystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record SettingsProfileUpdatedEvent(ScopedSystemId TargetSystemId, bool EmitUsernameUpdated) : ITargetedClusterEvent;

public sealed record SettingsAccountDeletedSignalEvent(ScopedSystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record SettingsAltersWipedSignalEvent(ScopedSystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record SettingsEncryptedDataWipedSignalEvent(ScopedSystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record SettingsDiscordAccountUnlinkedSignalEvent(ScopedSystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record SettingsAppleAccountUnlinkedSignalEvent(ScopedSystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record PollCreatedEvent(ScopedSystemId TargetSystemId, PollId PollId) : ITargetedClusterEvent;

public sealed record PollUpdatedEvent(ScopedSystemId TargetSystemId, PollId PollId) : ITargetedClusterEvent;

public sealed record PollDeletedEvent(ScopedSystemId TargetSystemId, PollId PollId) : ITargetedClusterEvent;

public sealed record GlobalJournalEntryCreatedEvent(ScopedSystemId TargetSystemId, EntryId EntryId) : ITargetedClusterEvent;

public sealed record GlobalJournalEntryUpdatedEvent(ScopedSystemId TargetSystemId, EntryId EntryId) : ITargetedClusterEvent;

public sealed record GlobalJournalEntryDeletedEvent(ScopedSystemId TargetSystemId, EntryId EntryId) : ITargetedClusterEvent;

public sealed record AlterJournalEntryCreatedEvent(ScopedSystemId TargetSystemId, EntryId EntryId) : ITargetedClusterEvent;

public sealed record AlterJournalEntryUpdatedEvent(ScopedSystemId TargetSystemId, EntryId EntryId) : ITargetedClusterEvent;

public sealed record AlterJournalEntryDeletedEvent(ScopedSystemId TargetSystemId, EntryId EntryId) : ITargetedClusterEvent;

public sealed record FriendshipAddedEvent(ScopedSystemId TargetSystemId, SystemId SystemId) : ITargetedClusterEvent;

public sealed record FriendshipRemovedEvent(ScopedSystemId TargetSystemId, SystemId SystemId) : ITargetedClusterEvent;

public sealed record FriendshipTrustedEvent(ScopedSystemId TargetSystemId, SystemId SystemId) : ITargetedClusterEvent;

public sealed record FriendshipUntrustedEvent(ScopedSystemId TargetSystemId, SystemId SystemId) : ITargetedClusterEvent;

public sealed record FriendRequestSentEvent(ScopedSystemId TargetSystemId, SystemId ToSystemId) : ITargetedClusterEvent;

public sealed record FriendRequestReceivedEvent(ScopedSystemId TargetSystemId, SystemId FromSystemId) : ITargetedClusterEvent;

public sealed record FriendRequestRemovedFromEvent(ScopedSystemId TargetSystemId, SystemId FromSystemId) : ITargetedClusterEvent;

public sealed record FriendRequestRemovedToEvent(ScopedSystemId TargetSystemId, SystemId ToSystemId) : ITargetedClusterEvent;

// One event per provider (mirrors the per-provider unlink signal events below) so the
// identity carries its real type and neither producer nor consumer switches on a provider enum.
public sealed record SettingsDiscordAccountLinkedEvent(ScopedSystemId TargetSystemId, DiscordId DiscordId) : ITargetedClusterEvent;

public sealed record SettingsGoogleAccountLinkedEvent(ScopedSystemId TargetSystemId, Email Email) : ITargetedClusterEvent;

public sealed record SettingsAppleAccountLinkedEvent(ScopedSystemId TargetSystemId, AppleId AppleId) : ITargetedClusterEvent;

public sealed record SettingsGoogleAccountUnlinkedSignalEvent(ScopedSystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record SettingsTagsWipedSignalEvent(ScopedSystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record SimplyPluralImportCompletedEvent(ScopedSystemId TargetSystemId, int AlterCount) : ITargetedClusterEvent;

public sealed record SimplyPluralImportFailedEvent(ScopedSystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record PluralKitImportCompletedEvent(ScopedSystemId TargetSystemId, int AlterCount) : ITargetedClusterEvent;

public sealed record PluralKitImportFailedEvent(ScopedSystemId TargetSystemId) : ITargetedClusterEvent;
