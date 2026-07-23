using Interfold.Contracts.Ids;

namespace Interfold.Contracts.Events;

public sealed record SettingsFieldsChangedEvent(ScopedSystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record SettingsProfileUpdatedEvent(ScopedSystemId TargetSystemId, bool EmitUsernameUpdated) : ITargetedClusterEvent;

public sealed record SettingsAccountDeletedSignalEvent(ScopedSystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record SettingsAltersWipedSignalEvent(ScopedSystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record SettingsEncryptedDataWipedSignalEvent(ScopedSystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record SettingsDiscordAccountUnlinkedSignalEvent(ScopedSystemId TargetSystemId) : ITargetedClusterEvent;

public sealed record SettingsAppleAccountUnlinkedSignalEvent(ScopedSystemId TargetSystemId) : ITargetedClusterEvent;

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
