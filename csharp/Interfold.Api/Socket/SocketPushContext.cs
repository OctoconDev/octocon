using System.Collections.Concurrent;
using System.Net.WebSockets;
using Interfold.Contracts.Ids;
using Microsoft.Extensions.Logging;

namespace Interfold.Api.Socket;

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

    /// <summary>
    /// The scoped composite form of the socket's principal, parsed from the JWT sub
    /// inside <c>WebSocketHandler.IsSocketJoinTokenAuthorizedAsync</c>. Fed to
    /// <see cref="Interfold.Domain.Abstractions.IClusterEventBus.SubscribeAsync{TEvent}"/>
    /// by <see cref="SocketEventPumpRunner.RunAllAsync"/> so the pump's ~38
    /// subscriptions filter on the scoped composite rather than the raw topic id.
    /// This is the only "who is this socket bound to" surface on the context — socket
    /// event handlers route on <c>evt.TargetSystemId</c> fed into
    /// <see cref="TryGetSystemTopic"/>, never on the context's own bound id.
    ///
    /// <para>
    /// Nullable because it's <see langword="null"/> for anonymous sockets that never
    /// completed a system-topic join. Every downstream consumer handles null (the bus's
    /// target filter treats it as "no filter").
    /// </para>
    /// </summary>
    public ScopedSystemId? JoinedScopedSystemId { get; }
    public ConcurrentDictionary<string, byte> JoinedTopics { get; }
    public ConcurrentDictionary<string, string?> TopicJoinReference { get; }
    public ConcurrentDictionary<string, bool> TopicReplyAsArrayFrame { get; }
    public SemaphoreSlim SendGate { get; }
    public CancellationToken CancellationToken { get; }
    /// <summary>
    /// The origin of the HTTP request that upgraded to this WebSocket
    /// (e.g. <c>https://api.example.com</c>). Used to qualify relative avatar URLs.
    /// </summary>
    public string? RequestOrigin { get; }

    public ILogger? Logger { get; }

    /// <summary>
    /// Time source for socket event handlers that need to synthesize a "now"-ish timestamp
    /// (e.g. <c>FriendshipSocketEventHandlers</c>'s fallback placeholder models when the
    /// authoritative row hasn't landed yet). Defaults to <see cref="TimeProvider.System"/>
    /// so callers that construct the context without wiring one (unit-test scaffolds) keep
    /// working; production paths inject the DI-registered singleton so time-freezing tests
    /// stay possible.
    /// </summary>
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

    /// <summary>
    /// Sends <paramref name="payload"/> to the socket's system topic only when the socket
    /// has joined that topic; silently no-ops otherwise. Use this in event handlers that
    /// consist of nothing but the four-line topic-guard followed by a single
    /// <see cref="SendAsync{TPayload}"/> call, collapsing both lines into one expression.
    /// </summary>
    public Task SendIfJoinedAsync<TPayload>(SystemId targetSystemId, string eventName, TPayload payload)
    {
        if (!TryGetSystemTopic(targetSystemId, out var topic, out var joinRef, out var asArray))
            return Task.CompletedTask;
        return SendAsync(topic, joinRef, asArray, eventName, payload);
    }

    /// <summary>
    /// Fetches an entity and, when present, wraps it into a socket payload and sends it on
    /// the joined system topic. Silently no-ops when the socket hasn't joined the target
    /// topic or when <paramref name="fetch"/> returns <see langword="null"/>.
    /// </summary>
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
