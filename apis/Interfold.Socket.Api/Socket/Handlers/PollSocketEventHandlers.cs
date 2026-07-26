using Interfold.Polls.Contracts;
using Interfold.Polls.Contracts.Events;
using Interfold.Polls.Contracts.Ids;
using Interfold.Polls.Contracts.Models.Read;
using Interfold.Polls.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;
using Interfold.Socket.Contracts;

namespace Interfold.Socket.Api.Socket.Handlers;

public static class PollSocketEventHandlers
{
    public static async Task HandleAsync(PollCreatedEvent evt, SocketPushContext context, IPollRepository pollRepository)
        => await HandleUpsertAsync(evt.TargetSystemId, evt.PollId, SocketEventNames.Polls.Created, context, pollRepository);

    public static async Task HandleAsync(PollUpdatedEvent evt, SocketPushContext context, IPollRepository pollRepository)
        => await HandleUpsertAsync(evt.TargetSystemId, evt.PollId, SocketEventNames.Polls.Updated, context, pollRepository);

    public static async Task HandleAsync(PollDeletedEvent evt, SocketPushContext context)
    {
        await context.SendIfJoinedAsync(evt.TargetSystemId, SocketEventNames.Polls.Deleted, new PollDeletedSocketPayload(evt.PollId));
    }

    private static Task HandleUpsertAsync(SystemId systemId, PollId pollId, string eventName, SocketPushContext context, IPollRepository pollRepository)
        => context.PushIfJoinedAsync<PollReadModel, PollSocketPayload>(
            systemId, eventName,
            ct => pollRepository.GetAsync(systemId, pollId, ct),
            poll => new PollSocketPayload(poll));
}
