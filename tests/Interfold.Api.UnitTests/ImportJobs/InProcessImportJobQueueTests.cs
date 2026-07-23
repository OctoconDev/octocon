using Interfold.Contracts.Ids;
using Interfold.Contracts.Models.ImportOperations;
using Interfold.Domain.Abstractions.ImportJobs;
using Interfold.Infrastructure.Coordination;

namespace Interfold.Api.UnitTests.ImportJobs;

// FIFO delivery, cancellation, back-pressure for InProcessImportJobQueue. Per-(system,
// kind) dedupe lives on IImportOperationRepository, not here.
public sealed class InProcessImportJobQueueTests
{
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
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        await Task.Delay(50);
        cts.Cancel();

        var completed = await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(5)));
        await Assert.That(completed).IsEqualTo((Task)reader)
            .Because("Cancelling the token while ReadAllAsync is parked on an empty channel must surface as a prompt exit; otherwise the worker would hang on shutdown.");
    }

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
