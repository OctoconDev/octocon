using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Interfold.Domain.Abstractions.ImportJobs;

namespace Interfold.Infrastructure.Coordination;

/// <summary>Single-channel <see cref="IImportJobQueue"/>. Bounded at
/// <see cref="DefaultCapacity"/>; enqueues wait rather than drop (a silent drop would
/// strand a claimed operation row without a worker run). Per-system serialisation lives
/// in the LWT mutex on <c>active_import_by_system</c>, so one channel + one worker is
/// sufficient.</summary>
public sealed class InProcessImportJobQueue : IImportJobQueue, IAsyncDisposable
{
    private const int DefaultCapacity = 256;

    private readonly Channel<ImportJobItem> _channel;

    public InProcessImportJobQueue()
    {
        _channel = Channel.CreateBounded<ImportJobItem>(new BoundedChannelOptions(DefaultCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public ValueTask EnqueueAsync(ImportJobItem item, CancellationToken cancellationToken = default)
    {
        return _channel.Writer.WriteAsync(item, cancellationToken);
    }

    public async IAsyncEnumerable<ImportJobItem> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
