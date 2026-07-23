using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Shared.Domain.Abstractions.Repository;

namespace Interfold.Api.Socket.Handlers;

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
