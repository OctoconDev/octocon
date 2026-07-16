using System.Collections.Concurrent;
using System.Threading.Channels;
using Interfold.Contracts.Events;
using Interfold.Contracts.Ids;
using Interfold.Domain.Abstractions;

namespace Interfold.Infrastructure.Coordination;

/// <summary>
/// In-process implementation of <see cref="IClusterEventBus"/> backed by
/// <see cref="System.Threading.Channels"/>.
/// <para>
/// Each event type gets its own bag of subscriptions. A subscription may be scoped to a
/// specific <c>TargetSystemId</c>; the bus filters at publish time so that only writers whose
/// target matches (or is null, meaning broadcast) receive the event. Events whose runtime
/// type does not implement <see cref="ITargetedClusterEvent"/> are delivered to all writers
/// regardless of scoping value.
/// </para>
/// <para>
/// Channels are configured with <c>AllowSynchronousContinuations = false</c> so a publisher
/// never runs a subscriber's continuation inline. Combined with the targeted publish-time
/// filter (so the bus only wakes sockets that actually care about the event), this keeps
/// the command-handler / HTTP request thread free to return to its caller as soon as the
/// event is enqueued — every WebSocket push handler runs on a separate threadpool turn.
/// </para>
/// <para>
/// This is the single-node equivalent of <c>Phoenix.PubSub</c> from the legacy runtime.
/// </para>
/// </summary>
public sealed class InProcessEventBus : IClusterEventBus, IDisposable
{
    // Non-generic interface so _topics can store typed bags without casting gymnastics.
    private interface ITopicBag
    {
        void CompleteAll();
    }

    // TargetSystemId is a ScopedSystemId so the subscriber-side wire shape matches the
    // publisher-side ITargetedClusterEvent.TargetSystemId at the type level — no
    // strip-and-compare needed in PublishAsync.
    private sealed record Subscription<TEvent>(ChannelWriter<TEvent> Writer, ScopedSystemId? TargetSystemId)
        where TEvent : class;

    private sealed class TopicBag<TEvent> : ITopicBag where TEvent : class
    {
        // Keyed on the writer so the enumerator can remove its own subscription on disposal
        // without needing a separate id.
        public readonly ConcurrentDictionary<ChannelWriter<TEvent>, Subscription<TEvent>> Subscriptions = new();

        public void CompleteAll()
        {
            foreach (var sub in Subscriptions.Values)
            {
                sub.Writer.TryComplete();
            }

            Subscriptions.Clear();
        }
    }

    private readonly ConcurrentDictionary<Type, ITopicBag> _topics = new();

    private TopicBag<TEvent> GetOrCreateBag<TEvent>() where TEvent : class
        => (TopicBag<TEvent>)_topics.GetOrAdd(typeof(TEvent), _ => new TopicBag<TEvent>());

    public async ValueTask PublishAsync<TEvent>(TEvent evt, CancellationToken ct = default)
        where TEvent : class
    {
        if (!_topics.TryGetValue(typeof(TEvent), out var bag))
        {
            return; // no subscribers — drop the event
        }

        var topicBag = (TopicBag<TEvent>)bag;
        // Pull the targeted-event payload once. Non-targeted events get null and bypass filtering.
        var targetedEvent = evt as ITargetedClusterEvent;

        foreach (var subscription in topicBag.Subscriptions.Values)
        {
            // Subscription with a non-null TargetSystemId only sees events whose target
            // matches. Subscription with a null TargetSystemId is broadcast.
            //
            // Both sides speak ScopedSystemId, so the compare is scoped-record-struct
            // equality — a cross-region collision (same rawId, different region) is
            // correctly treated as a different user.
            if (subscription.TargetSystemId is not null
                && targetedEvent is not null
                && subscription.TargetSystemId.Value != targetedEvent.TargetSystemId)
            {
                continue;
            }

            try
            {
                await subscription.Writer.WriteAsync(evt, ct).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                topicBag.Subscriptions.TryRemove(subscription.Writer, out _);
            }
        }
    }

    /// <summary>
    /// Broadcast subscription convenience overload. Kept as an instance method (in addition to
    /// the default interface implementation) so callers that hold a reference typed as
    /// <see cref="InProcessEventBus"/> rather than <see cref="IClusterEventBus"/> (e.g. test
    /// fixtures that <c>new</c> the bus directly) can still subscribe without explicit casting.
    /// </summary>
    public IAsyncEnumerable<TEvent> SubscribeAsync<TEvent>(CancellationToken ct = default)
        where TEvent : class
        => SubscribeAsync<TEvent>(targetSystemId: null, ct);

    public IAsyncEnumerable<TEvent> SubscribeAsync<TEvent>(
        ScopedSystemId? targetSystemId,
        CancellationToken ct = default)
        where TEvent : class
    {
        var channel = Channel.CreateUnbounded<TEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            // Force async dispatch: the publisher's PublishAsync only enqueues into the
            // channel and never runs the subscriber's handler on its own thread. This keeps
            // long-running socket push work (DB lookup + WebSocket SendAsync through the
            // per-socket SemaphoreSlim gate) off the command-handler / HTTP request thread
            // that originated the publish, which is critical when several sockets subscribe
            // to the same event type under parallel test load. Allowing synchronous
            // continuations let one slow subscriber stall the entire foreach in PublishAsync
            // and made tail-publish latency a function of subscriber count, defeating the
            // point of an event bus.
            AllowSynchronousContinuations = false
        });

        var topicBag = GetOrCreateBag<TEvent>();
        topicBag.Subscriptions.TryAdd(channel.Writer, new Subscription<TEvent>(channel.Writer, targetSystemId));

        return new ChannelAsyncEnumerable<TEvent>(channel, topicBag, ct);
    }

    private sealed class ChannelAsyncEnumerable<TEvent> : IAsyncEnumerable<TEvent>
        where TEvent : class
    {
        private readonly Channel<TEvent> _channel;
        private readonly TopicBag<TEvent> _topicBag;
        private readonly CancellationToken _ct;

        public ChannelAsyncEnumerable(Channel<TEvent> channel, TopicBag<TEvent> topicBag, CancellationToken ct)
        {
            _channel = channel;
            _topicBag = topicBag;
            _ct = ct;
        }

        public IAsyncEnumerator<TEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            // Combine the provided cancellation token with the one from construction
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_ct, cancellationToken);
            return new ChannelAsyncEnumerator(_channel, _topicBag, linkedCts);
        }

        private sealed class ChannelAsyncEnumerator : IAsyncEnumerator<TEvent>, IAsyncDisposable
        {
            private readonly Channel<TEvent> _channel;
            private readonly TopicBag<TEvent> _topicBag;
            private readonly ChannelReader<TEvent> _reader;
            private readonly ChannelWriter<TEvent> _writer;
            private readonly CancellationTokenSource _cts;
            private IAsyncEnumerator<TEvent>? _enumerator;
            private bool _disposed;

            public ChannelAsyncEnumerator(Channel<TEvent> channel, TopicBag<TEvent> topicBag, CancellationTokenSource cts)
            {
                _channel = channel;
                _topicBag = topicBag;
                _reader = channel.Reader;
                _writer = channel.Writer;
                _cts = cts;
                _enumerator = _reader.ReadAllAsync(_cts.Token).GetAsyncEnumerator();
            }

            public TEvent Current => _enumerator!.Current;

            public async ValueTask<bool> MoveNextAsync()
            {
                if (_disposed) return false;
                try
                {
                    return await _enumerator!.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            public async ValueTask DisposeAsync()
            {
                if (_disposed) return;
                _disposed = true;
                _topicBag.Subscriptions.TryRemove(_writer, out _);
                _writer.TryComplete();
                if (_enumerator != null)
                {
                    await _enumerator.DisposeAsync().ConfigureAwait(false);
                    _enumerator = null;
                }
                _cts.Cancel();
                _cts.Dispose();
            }
        }
    }

    public void Dispose()
    {
        foreach (var bag in _topics.Values)
            bag.CompleteAll();

        _topics.Clear();
    }
}
