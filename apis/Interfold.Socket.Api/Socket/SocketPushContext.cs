using System.Collections.Concurrent;
using System.Net.WebSockets;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Socket.Api.Socket;

public sealed class SocketPushContext
{
    public SocketPushContext(
        WebSocket socket,
        ScopedSystemId? joinedScopedSystemId,
        ConcurrentDictionary<string, byte> joinedTopics,
        ConcurrentDictionary<string, string?> topicJoinReference,
        ConcurrentDictionary<string, bool> topicReplyAsArrayFrame,
        SemaphoreSlim sendGate,
        CancellationToken cancellationToken,
        string? requestOrigin = null,
        ILogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        Socket = socket;
        JoinedScopedSystemId = joinedScopedSystemId;
        JoinedTopics = joinedTopics;
        TopicJoinReference = topicJoinReference;
        TopicReplyAsArrayFrame = topicReplyAsArrayFrame;
        SendGate = sendGate;
        CancellationToken = cancellationToken;
        RequestOrigin = requestOrigin;
        Logger = logger;
        TimeProvider = timeProvider ?? TimeProvider.System;
    }

    public WebSocket Socket { get; }

    /// <summary>Scoped composite of the socket's principal, fed to
    /// <see cref="Interfold.Shared.Domain.Abstractions.IClusterEventBus.SubscribeAsync{TEvent}"/>
    /// so pump subscriptions filter on it. Null for anonymous / unjoined sockets (bus
    /// treats null as "no filter").</summary>
    public ScopedSystemId? JoinedScopedSystemId { get; }
    public ConcurrentDictionary<string, byte> JoinedTopics { get; }
    public ConcurrentDictionary<string, string?> TopicJoinReference { get; }
    public ConcurrentDictionary<string, bool> TopicReplyAsArrayFrame { get; }
    public SemaphoreSlim SendGate { get; }
    public CancellationToken CancellationToken { get; }
    /// <summary>Origin of the upgrading HTTP request; used to qualify relative avatar URLs.</summary>
    public string? RequestOrigin { get; }

    public ILogger? Logger { get; }

    /// <summary>Defaults to <see cref="TimeProvider.System"/> so unit-test scaffolds work;
    /// production paths inject the DI singleton for time-freezing tests.</summary>
    public TimeProvider TimeProvider { get; }

    public bool TryGetSystemTopic(SystemId systemId, out string topic, out string? joinRef, out bool asArray)
    {
        topic = new SystemTopic(systemId).ToWireString();
        if (!JoinedTopics.ContainsKey(topic))
        {
            var matchedTopic = JoinedTopics.Keys.FirstOrDefault(t =>
                SystemTopic.TryParse(t, out var joined)
                && SystemTopic.IdMatches(joined.Id, systemId));

            if (matchedTopic is null)
            {
                joinRef = null;
                asArray = false;
                return false;
            }

            topic = matchedTopic;
        }

        TopicJoinReference.TryGetValue(topic, out joinRef);
        TopicReplyAsArrayFrame.TryGetValue(topic, out asArray);
        return true;
    }

    public Task SendAsync<TPayload>(string topic, string? joinRef, bool asArray, string eventName, TPayload payload)
        => WebSocketEvents.SendPhoenixPushAsync(
            Socket,
            topic,
            joinRef,
            eventName,
            payload,
            asArray,
            CancellationToken,
            SendGate);

    /// <summary>Sends <paramref name="payload"/> when the socket has joined the target's
    /// topic; no-op otherwise.</summary>
    public Task SendIfJoinedAsync<TPayload>(SystemId targetSystemId, string eventName, TPayload payload)
    {
        if (!TryGetSystemTopic(targetSystemId, out var topic, out var joinRef, out var asArray))
            return Task.CompletedTask;
        return SendAsync(topic, joinRef, asArray, eventName, payload);
    }

    /// <summary>Fetches and pushes when both the topic is joined and <paramref name="fetch"/>
    /// returns non-null.</summary>
    public async Task PushIfJoinedAsync<TEntity, TPayload>(
        SystemId systemId,
        string eventName,
        Func<CancellationToken, Task<TEntity?>> fetch,
        Func<TEntity, TPayload> wrap)
    {
        if (!TryGetSystemTopic(systemId, out var topic, out var joinRef, out var asArray))
            return;

        var entity = await fetch(CancellationToken).ConfigureAwait(false);
        if (entity is null)
            return;

        await SendAsync(topic, joinRef, asArray, eventName, wrap(entity));
    }
}
