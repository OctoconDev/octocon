namespace Interfold.Domain.Abstractions.ImportJobs;

/// <summary>In-process FIFO import-job queue. Handlers enqueue after LWT slot claim;
/// <c>ImportJobBackgroundService</c> consumes. Cross-replica dedupe lives in the
/// per-system LWT mutex, so a local channel is sufficient.</summary>
public interface IImportJobQueue
{
    /// <summary>Buffers <paramref name="item"/>. Bounded channel — enforces back-pressure
    /// against runaway producers, not normal-use throttling.</summary>
    ValueTask EnqueueAsync(ImportJobItem item, CancellationToken cancellationToken = default);

    /// <summary>FIFO consumption. One IImportJobQueue → one consuming worker.</summary>
    IAsyncEnumerable<ImportJobItem> ReadAllAsync(CancellationToken cancellationToken = default);
}
