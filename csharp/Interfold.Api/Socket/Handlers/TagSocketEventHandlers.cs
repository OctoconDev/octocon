using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Ids;
using Interfold.Domain.Abstractions.Repository;

namespace Interfold.Api.Socket.Handlers;

public static class TagSocketEventHandlers
{
    public static async Task HandleAsync(TagCreatedEvent evt, SocketPushContext context, ITagRepository tagRepository)
        => await HandleUpsertAsync(evt.TargetSystemId, evt.TagId, SocketEventNames.Tags.Created, context, tagRepository);

    public static async Task HandleAsync(TagUpdatedEvent evt, SocketPushContext context, ITagRepository tagRepository)
        => await HandleUpsertAsync(evt.TargetSystemId, evt.TagId, SocketEventNames.Tags.Updated, context, tagRepository);

    public static async Task HandleAsync(TagDeletedEvent evt, SocketPushContext context)
    {
        if (!context.TryGetSystemTopic(evt.TargetSystemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        await context.SendAsync(topic, joinRef, asArray, SocketEventNames.Tags.Deleted, new TagDeletedSocketPayload(evt.TagId));
    }

    private static async Task HandleUpsertAsync(SystemId systemId, TagId tagId, string eventName, SocketPushContext context, ITagRepository tagRepository)
    {
        if (!context.TryGetSystemTopic(systemId, out var topic, out var joinRef, out var asArray))
        {
            return;
        }

        var tag = await tagRepository.GetAsync(systemId, tagId, context.CancellationToken).ConfigureAwait(false);
        if (tag is null)
        {
            return;
        }

        await context.SendAsync(topic, joinRef, asArray, eventName, new TagSocketPayload(tag));
    }
}
