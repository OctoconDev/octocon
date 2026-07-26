using Interfold.Alters.Contracts.Events;
using Interfold.Alters.Domain.Abstractions.Repository;
using Interfold.Friendships.Contracts.Events;
using Interfold.Friendships.Domain.Abstractions.Repository;
using Interfold.Fronting.Contracts.Events;
using Interfold.Fronting.Domain.Abstractions.Repository;
using Interfold.Journals.Contracts.Events;
using Interfold.Journals.Domain.Abstractions.Repository;
using Interfold.Polls.Contracts.Events;
using Interfold.Polls.Domain.Abstractions.Repository;
using Interfold.Settings.Contracts.Events;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Socket.Api.Socket.Handlers;
using Interfold.Tags.Contracts.Events;
using Interfold.Tags.Domain.Abstractions.Repository;

namespace Interfold.Socket.Api.Socket;

public static class SocketEventPumpRunner
{
    public static Task RunAllAsync(
        IClusterEventBus eventBus,
        SocketPushContext context,
        IFrontingRepository frontingRepository,
        IAlterRepository alterRepository,
        ITagRepository tagRepository,
        ISettingsFieldRepository settingsFieldRepository,
        IAccountRepository accountRepository,
        IFriendshipRepository friendshipRepository,
        IPollRepository pollRepository,
        IJournalRepository journalRepository,
        IEncryptionStateRepository encryptionStateRepository)
    {
        // All subscriptions filter on context.JoinedScopedSystemId (ITargetedClusterEvent
        // → only this user's publishes fire).
        return Task.WhenAll(
            SubscribeAsync<FrontingStartedEvent>(eventBus, context, evt => FrontingSocketEventHandlers.HandleAsync(evt, context, frontingRepository)),
            SubscribeAsync<FrontingEndedEvent>(eventBus, context, evt => FrontingSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<FrontingSetEvent>(eventBus, context, evt => FrontingSocketEventHandlers.HandleAsync(evt, context, frontingRepository)),
            SubscribeAsync<FrontingBulkUpdatedEvent>(eventBus, context, evt => FrontingSocketEventHandlers.HandleAsync(evt, context, frontingRepository)),
            SubscribeAsync<FrontCommentUpdatedEvent>(eventBus, context, evt => FrontingSocketEventHandlers.HandleAsync(evt, context, frontingRepository)),
            SubscribeAsync<FrontingPrimaryChangedEvent>(eventBus, context, evt => FrontingSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<FrontDeletedEvent>(eventBus, context, evt => FrontingSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<AlterCreatedEvent>(eventBus, context, evt => AlterSocketEventHandlers.HandleAsync(evt, context, alterRepository)),
            SubscribeAsync<AlterUpdatedEvent>(eventBus, context, evt => AlterSocketEventHandlers.HandleAsync(evt, context, alterRepository)),
            SubscribeAsync<AlterDeletedEvent>(eventBus, context, evt => AlterSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<TagCreatedEvent>(eventBus, context, evt => TagSocketEventHandlers.HandleAsync(evt, context, tagRepository)),
            SubscribeAsync<TagUpdatedEvent>(eventBus, context, evt => TagSocketEventHandlers.HandleAsync(evt, context, tagRepository)),
            SubscribeAsync<TagDeletedEvent>(eventBus, context, evt => TagSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<SettingsFieldsChangedEvent>(eventBus, context, evt => SettingsSocketEventHandlers.HandleAsync(evt, context, settingsFieldRepository)),
            SubscribeAsync<SettingsProfileUpdatedEvent>(eventBus, context, evt => SettingsSocketEventHandlers.HandleAsync(evt, context, accountRepository, alterRepository, frontingRepository, settingsFieldRepository, encryptionStateRepository)),
            SubscribeAsync<SettingsAccountDeletedSignalEvent>(eventBus, context, evt => SettingsSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<SettingsAltersWipedSignalEvent>(eventBus, context, evt => SettingsSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<SettingsEncryptedDataWipedSignalEvent>(eventBus, context, evt => SettingsSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<SettingsDiscordAccountUnlinkedSignalEvent>(eventBus, context, evt => SettingsSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<SettingsAppleAccountUnlinkedSignalEvent>(eventBus, context, evt => SettingsSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<SettingsDiscordAccountLinkedEvent>(eventBus, context, evt => SettingsSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<SettingsGoogleAccountLinkedEvent>(eventBus, context, evt => SettingsSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<SettingsAppleAccountLinkedEvent>(eventBus, context, evt => SettingsSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<FriendshipAddedEvent>(eventBus, context, evt => FriendshipSocketEventHandlers.HandleAsync(evt, context, friendshipRepository)),
            SubscribeAsync<FriendshipRemovedEvent>(eventBus, context, evt => FriendshipSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<FriendshipTrustedEvent>(eventBus, context, evt => FriendshipSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<FriendshipUntrustedEvent>(eventBus, context, evt => FriendshipSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<FriendRequestSentEvent>(eventBus, context, evt => FriendshipSocketEventHandlers.HandleAsync(evt, context, friendshipRepository)),
            SubscribeAsync<FriendRequestReceivedEvent>(eventBus, context, evt => FriendshipSocketEventHandlers.HandleAsync(evt, context, friendshipRepository)),
            SubscribeAsync<FriendRequestRemovedFromEvent>(eventBus, context, evt => FriendshipSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<FriendRequestRemovedToEvent>(eventBus, context, evt => FriendshipSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<PollCreatedEvent>(eventBus, context, evt => PollSocketEventHandlers.HandleAsync(evt, context, pollRepository)),
            SubscribeAsync<PollUpdatedEvent>(eventBus, context, evt => PollSocketEventHandlers.HandleAsync(evt, context, pollRepository)),
            SubscribeAsync<PollDeletedEvent>(eventBus, context, evt => PollSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<GlobalJournalEntryCreatedEvent>(eventBus, context, evt => JournalSocketEventHandlers.HandleAsync(evt, context, journalRepository)),
            SubscribeAsync<GlobalJournalEntryUpdatedEvent>(eventBus, context, evt => JournalSocketEventHandlers.HandleAsync(evt, context, journalRepository)),
            SubscribeAsync<GlobalJournalEntryDeletedEvent>(eventBus, context, evt => JournalSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<AlterJournalEntryCreatedEvent>(eventBus, context, evt => JournalSocketEventHandlers.HandleAsync(evt, context, journalRepository)),
            SubscribeAsync<AlterJournalEntryUpdatedEvent>(eventBus, context, evt => JournalSocketEventHandlers.HandleAsync(evt, context, journalRepository)),
            SubscribeAsync<AlterJournalEntryDeletedEvent>(eventBus, context, evt => JournalSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<SettingsGoogleAccountUnlinkedSignalEvent>(eventBus, context, evt => SettingsSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<SettingsTagsWipedSignalEvent>(eventBus, context, evt => SettingsSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<SimplyPluralImportCompletedEvent>(eventBus, context, evt => ImportSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<SimplyPluralImportFailedEvent>(eventBus, context, evt => ImportSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<PluralKitImportCompletedEvent>(eventBus, context, evt => ImportSocketEventHandlers.HandleAsync(evt, context)),
            SubscribeAsync<PluralKitImportFailedEvent>(eventBus, context, evt => ImportSocketEventHandlers.HandleAsync(evt, context)));
    }

    private static async Task SubscribeAsync<TEvent>(
        IClusterEventBus eventBus,
        SocketPushContext context,
        Func<TEvent, Task> handler)
        where TEvent : class
    {
        // Wrap the per-event dispatch so a single handler exception can't kill the
        // subscription (transient cancel from a retry Task.Delay, socket write during
        // teardown, etc.). Cancellation still exits cleanly via MoveNextAsync.
        await foreach (var evt in eventBus.SubscribeAsync<TEvent>(context.JoinedScopedSystemId, context.CancellationToken).ConfigureAwait(false))
        {
            try
            {
                await handler(evt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                context.Logger?.LogError(
                    ex,
                    "Socket push handler for {EventType} threw. Topics={Topics}",
                    typeof(TEvent).Name,
                    string.Join(",", context.JoinedTopics.Keys));
            }
        }
    }
}
