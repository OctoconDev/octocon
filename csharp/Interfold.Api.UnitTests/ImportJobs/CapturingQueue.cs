using Interfold.Domain.Abstractions.ImportJobs;
using Interfold.Infrastructure.Coordination;

namespace Interfold.Api.UnitTests.ImportJobs;

/// <summary>
/// Decorator over the real <see cref="InProcessImportJobQueue"/> that captures every
/// enqueued <see cref="ImportJobItem"/> so tests can assert on what was enqueued
/// without re-implementing the channel semantics. Shared by
/// <c>ImportSpCommandHandlerDispatchTests</c> and
/// <c>ImportPkCommandHandlerDispatchTests</c>; previously duplicated byte-for-byte in
/// each file.
/// </summary>
internal sealed class CapturingQueue : IImportJobQueue, IAsyncDisposable
{
    private readonly InProcessImportJobQueue _inner;
    private readonly List<ImportJobItem> _enqueued = new();
    private readonly object _gate = new();

    public CapturingQueue(InProcessImportJobQueue inner) { _inner = inner; }

    public IReadOnlyList<ImportJobItem> Enqueued
    {
        get { lock (_gate) return _enqueued.ToArray(); }
    }

    public ValueTask EnqueueAsync(ImportJobItem item, CancellationToken cancellationToken = default)
    {
        lock (_gate) _enqueued.Add(item);
        return _inner.EnqueueAsync(item, cancellationToken);
    }

    public IAsyncEnumerable<ImportJobItem> ReadAllAsync(CancellationToken cancellationToken = default)
        => _inner.ReadAllAsync(cancellationToken);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
