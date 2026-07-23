using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Read;
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
        await context.SendIfJoinedAsync(evt.TargetSystemId, SocketEventNames.Journals.GlobalDeleted, new EntryDeletedSocketPayload(evt.EntryId));
    }

    public static async Task HandleAsync(AlterJournalEntryCreatedEvent evt, SocketPushContext context, IJournalRepository journalRepository)
        => await HandleAlterUpsertAsync(evt.TargetSystemId, evt.EntryId, SocketEventNames.Journals.AlterCreated, context, journalRepository);

    public static async Task HandleAsync(AlterJournalEntryUpdatedEvent evt, SocketPushContext context, IJournalRepository journalRepository)
        => await HandleAlterUpsertAsync(evt.TargetSystemId, evt.EntryId, SocketEventNames.Journals.AlterUpdated, context, journalRepository);

    public static async Task HandleAsync(AlterJournalEntryDeletedEvent evt, SocketPushContext context)
    {
        await context.SendIfJoinedAsync(evt.TargetSystemId, SocketEventNames.Journals.AlterDeleted, new EntryDeletedSocketPayload(evt.EntryId));
    }

    private static Task HandleGlobalUpsertAsync(SystemId systemId, EntryId entryId, string eventName, SocketPushContext context, IJournalRepository journalRepository)
        => context.PushIfJoinedAsync<JournalReadModel, GlobalJournalSocketPayload>(
            systemId, eventName,
            ct => journalRepository.GetGlobalAsync(systemId, entryId, ct),
            entry => new GlobalJournalSocketPayload(entry));

    private static Task HandleAlterUpsertAsync(SystemId systemId, EntryId entryId, string eventName, SocketPushContext context, IJournalRepository journalRepository)
        => context.PushIfJoinedAsync<AlterJournalReadModel, AlterJournalSocketPayload>(
            systemId, eventName,
            ct => journalRepository.GetAlterAsync(systemId, entryId, ct),
            entry => new AlterJournalSocketPayload(entry));
}
