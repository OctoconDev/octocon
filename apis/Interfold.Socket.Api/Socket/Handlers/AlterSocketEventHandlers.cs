using Interfold.Api.Helpers;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Domain.Abstractions.Repository;

namespace Interfold.Api.Socket.Handlers;

public static class AlterSocketEventHandlers
{
    public static async Task HandleAsync(AlterCreatedEvent evt, SocketPushContext context, IAlterRepository alterRepository)
        => await HandleUpsertAsync(evt.TargetSystemId, evt.AlterId, SocketEventNames.Alters.Created, context, alterRepository);

    public static async Task HandleAsync(AlterUpdatedEvent evt, SocketPushContext context, IAlterRepository alterRepository)
        => await HandleUpsertAsync(evt.TargetSystemId, evt.AlterId, SocketEventNames.Alters.Updated, context, alterRepository);

    public static async Task HandleAsync(AlterDeletedEvent evt, SocketPushContext context)
    {
        await context.SendIfJoinedAsync(evt.TargetSystemId, SocketEventNames.Alters.Deleted, new AlterDeletedSocketPayload(evt.AlterId));
    }

    private static Task HandleUpsertAsync(SystemId systemId, AlterId alterId, string eventName, SocketPushContext context, IAlterRepository alterRepository)
        => context.PushIfJoinedAsync<AlterReadModel, AlterSocketPayload>(
            systemId, eventName,
            ct => alterRepository.GetAsync(systemId, alterId, ct),
            alter =>
            {
                alter.AvatarUrl = AvatarUrlQualifier.QualifyAvatar(alter, context.RequestOrigin);
                return new AlterSocketPayload(alter);
            });
}
