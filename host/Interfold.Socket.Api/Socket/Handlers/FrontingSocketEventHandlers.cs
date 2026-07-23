using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models.Read;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Api.Socket.Handlers;

public static class FrontingSocketEventHandlers
{
    public static Task HandleAsync(FrontingStartedEvent evt, SocketPushContext context, IFrontingRepository frontingRepository)
        => HandleFrontUpsertAsync(evt.TargetSystemId, evt.FrontId, SocketEventNames.Fronting.Started, context, frontingRepository);

    public static async Task HandleAsync(FrontingEndedEvent evt, SocketPushContext context)
    {
        await context.SendIfJoinedAsync(evt.TargetSystemId, SocketEventNames.Fronting.Ended, new AlterIdSocketPayload(evt.AlterId));
    }

    public static Task HandleAsync(FrontingSetEvent evt, SocketPushContext context, IFrontingRepository frontingRepository)
        => HandleFrontUpsertAsync(evt.TargetSystemId, evt.FrontId, SocketEventNames.Fronting.Set, context, frontingRepository);

    public static Task HandleAsync(FrontingBulkUpdatedEvent evt, SocketPushContext context, IFrontingRepository frontingRepository)
        => context.PushIfJoinedAsync<IReadOnlyList<FrontActiveReadModel>, FrontsSocketPayload>(
            evt.TargetSystemId,
            SocketEventNames.Fronting.BulkUpdated,
            ct => frontingRepository.ListActiveAsync(evt.TargetSystemId, ct),
            fronts => new FrontsSocketPayload(fronts));

    public static Task HandleAsync(FrontCommentUpdatedEvent evt, SocketPushContext context, IFrontingRepository frontingRepository)
        => HandleFrontUpsertAsync(evt.TargetSystemId, evt.FrontId, SocketEventNames.Fronting.CommentUpdated, context, frontingRepository);

    public static async Task HandleAsync(FrontingPrimaryChangedEvent evt, SocketPushContext context)
    {
        await context.SendIfJoinedAsync(evt.TargetSystemId, SocketEventNames.Fronting.PrimaryChanged, new AlterIdSocketPayload(evt.AlterId));
    }

    public static async Task HandleAsync(FrontDeletedEvent evt, SocketPushContext context)
    {
        await context.SendIfJoinedAsync(evt.TargetSystemId, SocketEventNames.Fronting.Deleted, new FrontIdSocketPayload(evt.FrontId));
    }

    private static Task HandleFrontUpsertAsync(SystemId systemId, FrontId frontId, string eventName, SocketPushContext context, IFrontingRepository frontingRepository)
        => context.PushIfJoinedAsync<FrontActiveReadModel, FrontSocketPayload>(
            systemId, eventName,
            ct => frontingRepository.GetActiveByFrontIdAsync(systemId, frontId, ct),
            front => new FrontSocketPayload(front));
}
