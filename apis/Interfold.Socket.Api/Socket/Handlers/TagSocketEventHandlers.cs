using Interfold.Shared.Contracts.Ids;
using Interfold.Socket.Contracts;
using Interfold.Tags.Contracts;
using Interfold.Tags.Contracts.Events;
using Interfold.Tags.Contracts.Ids;
using Interfold.Tags.Contracts.Models.Read;
using Interfold.Tags.Domain.Abstractions.Repository;

namespace Interfold.Socket.Api.Socket.Handlers;

public static class TagSocketEventHandlers
{
    public static async Task HandleAsync(TagCreatedEvent evt, SocketPushContext context, ITagRepository tagRepository)
        => await HandleUpsertAsync(evt.TargetSystemId, evt.TagId, SocketEventNames.Tags.Created, context, tagRepository);

    public static async Task HandleAsync(TagUpdatedEvent evt, SocketPushContext context, ITagRepository tagRepository)
        => await HandleUpsertAsync(evt.TargetSystemId, evt.TagId, SocketEventNames.Tags.Updated, context, tagRepository);

    public static async Task HandleAsync(TagDeletedEvent evt, SocketPushContext context)
    {
        await context.SendIfJoinedAsync(evt.TargetSystemId, SocketEventNames.Tags.Deleted, new TagDeletedSocketPayload(evt.TagId));
    }

    private static Task HandleUpsertAsync(SystemId systemId, TagId tagId, string eventName, SocketPushContext context, ITagRepository tagRepository)
        => context.PushIfJoinedAsync<TagReadModel, TagSocketPayload>(
            systemId, eventName,
            ct => tagRepository.GetAsync(systemId, tagId, ct),
            tag => new TagSocketPayload(tag));
}
