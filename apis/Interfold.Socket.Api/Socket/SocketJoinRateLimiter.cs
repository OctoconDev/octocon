using System.Collections.Concurrent;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Socket.Api.Socket;

public sealed class SocketJoinRateLimiter(TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _windows = new(StringComparer.Ordinal);

    public bool Allow(SystemId systemId)
    {
        var now = timeProvider.GetUtcNow();
        var queue = _windows.GetOrAdd(systemId, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            var cutoff = now.AddSeconds(-1);
            while (queue.Count > 0 && queue.Peek() < cutoff)
            {
                queue.Dequeue();
            }

            if (queue.Count >= 2)
            {
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }
}