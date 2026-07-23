using System.Collections.Concurrent;
using System.Threading.Channels;
using Interfold.Shared.Contracts.Events;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Infrastructure.Coordination;

/// <summary>Single-node <see cref="IClusterEventBus"/> backed by
/// <see cref="System.Threading.Channels"/>. Per-event-type subscription bags; targeted
/// subscriptions filtered at publish time. Channels use
/// <c>AllowSynchronousContinuations = false</c> so publisher threads never run subscriber
/// continuations inline (keeps HTTP-request threads free of socket-push latency).</summary>
public sealed class InProcessEventBus : IClusterEventBus, IDisposable
{
    private interface ITopicBag
    {
        void CompleteAll();
    }

    private sealed record Subscription<TEvent>(ChannelWriter<TEvent> Writer, ScopedSystemId? TargetSystemId)
        where TEvent : class;

    private sealed class TopicBag<TEvent> : ITopicBag where TEvent : class
    {
        // Keyed by writer so an enumerator can self-remove on disposal without a separate id.
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
            return;
        }

        var topicBag = (TopicBag<TEvent>)bag;
        var targetedEvent = evt as ITargetedClusterEvent;

        foreach (var subscription in topicBag.Subscriptions.Values)
        {
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

    /// <summary>Broadcast subscribe — instance-method overload so test fixtures that hold
    /// the concrete type don't need to cast to <see cref="IClusterEventBus"/>.</summary>
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
            // Async-only continuations — a slow subscriber must not stall the publisher's
            // foreach loop.
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
