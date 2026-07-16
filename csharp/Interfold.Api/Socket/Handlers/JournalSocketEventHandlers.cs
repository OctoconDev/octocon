using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Ids;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Api.Socket.Handlers;

public static class JournalSocketEventHandlers
{
    public static async Task HandleAsync(GlobalJournalEntryCreatedEvent evt, SocketPushContext context, IJournalRepository journalRepository)
        => await HandleGlobalUpsertAsync(evt.TargetSystemId, evt.EntryId, SocketEventNames.Journals.GlobalCreated, context, journalRepository);

    public static async Task HandleAsync(GlobalJournalEntryUpdatedEvent evt, SocketPushContext context, IJournalRepository journalRepository)
        => await HandleGlobalUpsertAsync(evt.TargetSystemId, evt.EntryId, SocketEventNames.Journals.GlobalUpdated, context, journalRepository);

    public static async Task HandleAsync(GlobalJournalEntryDeletedEvent evt, SocketPushContext context)
    {
        if (!context.TryGetSystemTopic(evt.TargetSystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Journals.GlobalDeleted, new EntryDeletedSocketPayload(evt.EntryId));
    }

    public static async Task HandleAsync(AlterJournalEntryCreatedEvent evt, SocketPushContext context, IJournalRepository journalRepository)
        => await HandleAlterUpsertAsync(evt.TargetSystemId, evt.EntryId, SocketEventNames.Journals.AlterCreated, context, journalRepository);

    public static async Task HandleAsync(AlterJournalEntryUpdatedEvent evt, SocketPushContext context, IJournalRepository journalRepository)
        => await HandleAlterUpsertAsync(evt.TargetSystemId, evt.EntryId, SocketEventNames.Journals.AlterUpdated, context, journalRepository);

    public static async Task HandleAsync(AlterJournalEntryDeletedEvent evt, SocketPushContext context)
    {
        if (!context.TryGetSystemTopic(evt.TargetSystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Journals.AlterDeleted, new EntryDeletedSocketPayload(evt.EntryId));
    }

    private static async Task HandleGlobalUpsertAsync(SystemId systemId, EntryId entryId, string eventName, SocketPushContext context, IJournalRepository journalRepository)
    {
        if (!context.TryGetSystemTopic(systemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var entry = await journalRepository.GetGlobalAsync(systemId, entryId, context.CancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return;
        }

        await context.SendAsync(topic, joinRef, asArray, eventName, new GlobalJournalSocketPayload(entry));
    }

    private static async Task HandleAlterUpsertAsync(SystemId systemId, EntryId entryId, string eventName, SocketPushContext context, IJournalRepository journalRepository)
    {
        if (!context.TryGetSystemTopic(systemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var entry = await journalRepository.GetAlterAsync(systemId, entryId, context.CancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return;
        }

        await context.SendAsync(topic, joinRef, asArray, eventName, new AlterJournalSocketPayload(entry));
    }
}
