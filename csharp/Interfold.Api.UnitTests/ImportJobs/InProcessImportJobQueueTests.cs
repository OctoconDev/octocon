using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.ImportOperations;
using Interfold.Domain.Abstractions.ImportJobs;
using Interfold.Infrastructure.Coordination;

namespace Interfold.Api.UnitTests.ImportJobs;

/// <summary>
/// Pins the contract of <see cref="InProcessImportJobQueue"/>. The queue is a thin wrapper
/// around <c>System.Threading.Channels.Channel&lt;T&gt;</c> but the contract that the rest
/// of the system depends on — FIFO delivery, cancellation observance, back-pressure rather
/// than drop — has to hold under change. These tests guard those properties.
///
/// <para>
/// <b>What the queue is NOT responsible for.</b> Per-(system, kind) deduplication is owned
/// by <see cref="Interfold.Domain.Abstractions.Repository.IImportOperationRepository"/>.
/// By the time an item lands in the queue, the repository has already guaranteed there is
/// at most one in-flight operation for that system. So we don't test dedupe here — those
/// tests live in <see cref="InMemoryImportOperationRepositoryTests"/>.
/// </para>
/// </summary>
public sealed class InProcessImportJobQueueTests
{
    /// <summary>
    /// Items consumed in the order they were enqueued. FIFO is load-bearing: the worker
    /// uses queue ordering to keep "older claim runs first" so the user's first click is
    /// reflected first.
    /// </summary>
    [Test]
    public async Task ReadAll_DeliversItemsInFifoOrder()
    {
        await using var queue = new InProcessImportJobQueue();
        var first = NewItem("nam:sys-a");
        var second = NewItem("nam:sys-b");
        var third = NewItem("nam:sys-c");

        await queue.EnqueueAsync(first);
        await queue.EnqueueAsync(second);
        await queue.EnqueueAsync(third);
        await queue.DisposeAsync();

        var observed = new List<ImportJobItem>();
        await foreach (var item in queue.ReadAllAsync())
        {
            observed.Add(item);
        }

        using (Assert.Multiple())
        {
            await Assert.That(observed).HasCount(3)
                .Because("All three enqueued items must be observed by the reader; lost items would orphan an operation row in Cassandra.");
            await Assert.That(observed[0]).IsEqualTo(first);
            await Assert.That(observed[1]).IsEqualTo(second);
            await Assert.That(observed[2]).IsEqualTo(third)
                .Because("Items must be delivered in the order they were enqueued so older claims are reflected first.");
        }
    }

    /// <summary>
    /// Reader sees items the producer publishes after subscription. This is the normal
    /// streaming shape the background service runs in — start consuming, then handle
    /// items as they arrive.
    /// </summary>
    [Test]
    public async Task ReadAll_StreamsItemsPublishedAfterReaderAttached()
    {
        await using var queue = new InProcessImportJobQueue();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        var observed = new List<ImportJobItem>();
        var reader = Task.Run(async () =>
        {
            try
            {
                await foreach (var item in queue.ReadAllAsync(cts.Token))
                {
                    observed.Add(item);
                    if (observed.Count == 2) break;
                }
            }
            catch (OperationCanceledException) { }
        });

        var first = NewItem("nam:sys-a");
        var second = NewItem("nam:sys-b");
        await queue.EnqueueAsync(first);
        await queue.EnqueueAsync(second);

        await reader;
        cts.Cancel();

        using (Assert.Multiple())
        {
            await Assert.That(observed).HasCount(2)
                .Because("Both items pushed after the reader attached must be observed; otherwise the worker would silently miss late-arriving jobs.");
            await Assert.That(observed[0]).IsEqualTo(first);
            await Assert.That(observed[1]).IsEqualTo(second);
        }
    }

    /// <summary>
    /// Cancelling the read loop completes the async sequence promptly. This is how the
    /// background service stops on host shutdown — its <c>ExecuteAsync</c> cancellation
    /// token is the one we pass to <c>ReadAllAsync</c>.
    /// </summary>
    [Test]
    public async Task ReadAll_StopsWhenCancellationTokenFires()
    {
        await using var queue = new InProcessImportJobQueue();
        using var cts = new CancellationTokenSource();

        var reader = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in queue.ReadAllAsync(cts.Token))
                {
                    // Intentionally consume nothing — we just want the loop to be active.
                }
            }
            catch (OperationCanceledException)
            {
                // Expected once the token fires while we're parked on ReadAsync.
            }
        });

        // Give the reader a moment to park on the empty channel, then cancel.
        await Task.Delay(50);
        cts.Cancel();

        var completed = await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(5)));
        await Assert.That(completed).IsEqualTo((Task)reader)
            .Because("Cancelling the token while ReadAllAsync is parked on an empty channel must surface as a prompt exit; otherwise the worker would hang on shutdown.");
    }

    /// <summary>
    /// <c>DisposeAsync</c> completes the writer side so any active reader observes the
    /// end of the sequence. This is how the channel is torn down on host shutdown without
    /// needing a separate cancellation signal.
    /// </summary>
    [Test]
    public async Task Dispose_CompletesReaderSequence()
    {
        var queue = new InProcessImportJobQueue();
        await queue.EnqueueAsync(NewItem("nam:sys-a"));

        await queue.DisposeAsync();

        var observed = 0;
        await foreach (var _ in queue.ReadAllAsync())
        {
            observed++;
        }

        await Assert.That(observed).IsEqualTo(1)
            .Because("After dispose, the reader must drain the buffered item and then complete cleanly rather than hang on the closed channel.");
    }

    private static ImportJobItem NewItem(string systemId) => new(
        new ImportOperationId(Guid.NewGuid()),
        ScopedSystemId.ParseScoped(systemId),
        ImportOperationKind.SimplyPlural,
        Token: new ImportToken("synthetic-token"),
        RecoveryCode: null);
}
