using Interfold.Api.Socket.Handlers;
using Interfold.Contracts.Events;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Microsoft.Extensions.Logging;

namespace Interfold.Api.Socket;

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
        // Every event subscription below is scoped to context.JoinedScopedSystemId via the bus's
        // targetSystemId filter. Because every event the pump cares about implements
        // ITargetedClusterEvent, the bus delivers an event to this socket only when its
        // TargetSystemId equals the socket's JoinedScopedSystemId — i.e. the per-socket pump
        // only does work when the publish is actually for this user.
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
        // Guard against a single handler exception killing the per-event subscription
        // loop. Without this, the await foreach would unwind on the first throw from
        // handler(evt) — e.g. a transient cancellation propagating out of a retry's
        // Task.Delay under thread-pool contention, or a socket write failing during a
        // teardown window — and every subsequent event of TEvent for this socket would
        // be silently dropped because the channel reader is gone. Under sustained
        // parallel-test load this manifested as Api_UserSocketEndpoint_PushesFriendTrust
        // missing its friend_request_received push on Scylla/Cassandra fixtures only.
        //
        // Keep the loop alive across handler faults; if cancellation comes through
        // context.CancellationToken we still exit cleanly because SubscribeAsync's
        // enumerator observes it and MoveNextAsync simply returns false.
        // SubscribeAsync takes ScopedSystemId?. Feed it the scoped composite that
        // WebSocketHandler parsed from the JWT sub — the scoped-to-scoped compare in
        // InProcessEventBus.PublishAsync is byte-equivalent when regions match and
        // correctly rejects cross-region false positives (same raw id, different region).
        await foreach (var evt in eventBus.SubscribeAsync<TEvent>(context.JoinedScopedSystemId, context.CancellationToken).ConfigureAwait(false))
        {
            try
            {
                await handler(evt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                // Context is shutting down; let the foreach observe cancellation
                // on its next MoveNextAsync rather than tearing down via throw.
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
