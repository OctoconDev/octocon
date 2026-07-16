using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.ImportOperations;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions.ImportJobs;
using Interfold.Domain.Settings;
using Interfold.Infrastructure.Coordination;
using Interfold.Infrastructure.InMemory.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Api.UnitTests.ImportJobs;

/// <summary>
/// Pins the async-dispatch contract of <see cref="ImportSpCommandHandler"/>. The handler
/// is the choke point that previously ran the entire SP importer synchronously — and
/// inside a Polly-retried POST that produced 2-3× duplicate imports on the Pi 4. These
/// tests pin its new shape:
///
/// <list type="bullet">
///   <item>A successful dispatch claims a slot via the repository and enqueues exactly one job.</item>
///   <item>A concurrent dispatch for the same system observes <c>Status = "running"</c>, returns the same operation_id, and does NOT enqueue a second job (the load-bearing dedupe invariant against the bug class).</item>
///   <item>An empty token is rejected without claiming a slot.</item>
/// </list>
/// </summary>
public sealed class ImportSpCommandHandlerDispatchTests
{
    // PrincipalId is a ScopedSystemId; ParseScoped enforces the wire-canonical form so
    // the test fixture matches what the middleware constructs at ingress.
    private static readonly ScopedSystemId SystemId
        = ScopedSystemId.ParseScoped("nam:sys-sp-dispatch-test");

    /// <summary>
    /// First dispatch for a clean system: handler returns <c>queued</c> and enqueues a job
    /// item carrying the correct kind + token. The operation_id in the result must equal
    /// the queued item's operation_id so the worker and the HTTP caller agree on the
    /// correlation handle.
    /// </summary>
    [Test]
    public async Task HandleAsync_FreshDispatch_ReturnsQueuedAndEnqueuesOneItem()
    {
        var (handler, queue, capturingQueue) = NewHandler();

        var result = await handler.HandleAsync(NewEnvelope("idem-1", token: "synthetic-token"));

        using (Assert.Multiple())
        {
            await Assert.That(result.Accepted).IsTrue()
                .Because("A valid token must accept; otherwise the controller would never reach the 202 branch.");
            await Assert.That(result.Result!.Status).IsEqualTo(ImportOperationDispatchStatus.Queued)
                .Because("A fresh claim must surface as 'queued' so the client (and operator logs) can distinguish it from a collapsed dispatch.");
            await Assert.That(capturingQueue.Enqueued).HasCount(1)
                .Because("Exactly one job must reach the queue per successful claim — more than one would reintroduce the duplicate-import bug class.");
            await Assert.That(capturingQueue.Enqueued[0].Kind).IsEqualTo(ImportOperationKind.SimplyPlural);
            await Assert.That(capturingQueue.Enqueued[0].OperationId).IsEqualTo(result.Result!.OperationId)
                .Because("The handler's returned operation_id must match the queued item's id, so the worker writes the row the controller advertised.");
        }

        await ((IAsyncDisposable)queue).DisposeAsync();
    }

    /// <summary>
    /// A second dispatch arriving while the first is still in flight (no terminal yet)
    /// must collapse — same operation_id, no second enqueue. This is the test that
    /// directly guards against the duplicate-SP-import bug class: any number of repeated
    /// HTTP attempts (Polly retry, browser retry, double click) produce exactly one run.
    /// </summary>
    [Test]
    public async Task HandleAsync_ConcurrentDispatch_CollapsesOntoSameOperationAndDoesNotReenqueue()
    {
        var (handler, queue, capturingQueue) = NewHandler();

        var first = await handler.HandleAsync(NewEnvelope("idem-1", token: "synthetic-token"));
        var second = await handler.HandleAsync(NewEnvelope("idem-2", token: "synthetic-token"));

        using (Assert.Multiple())
        {
            await Assert.That(second.Accepted).IsTrue();
            await Assert.That(second.Result!.OperationId).IsEqualTo(first.Result!.OperationId)
                .Because("A collapsed dispatch must return the existing operation_id; otherwise the client would subscribe to a frame that never arrives.");
            await Assert.That(second.Result!.Status).IsEqualTo(ImportOperationDispatchStatus.Running)
                .Because("The collapsed status is the agreed signal to the controller that it's a no-op — operator logs and audit trails depend on this string.");
            await Assert.That(capturingQueue.Enqueued).HasCount(1)
                .Because("Only the original click may enqueue a job; the duplicate dispatch must collapse without producing a second worker run.");
        }

        await ((IAsyncDisposable)queue).DisposeAsync();
    }

    /// <summary>
    /// Empty token is invalid input — handler rejects before touching the repository or
    /// the queue. This avoids burning a slot on garbage input and keeps the conflict
    /// shape consistent with the rest of the settings command surface.
    /// </summary>
    [Test]
    public async Task HandleAsync_EmptyToken_RejectsWithoutClaimingOrEnqueuing()
    {
        var (handler, queue, capturingQueue) = NewHandler();

        var result = await handler.HandleAsync(NewEnvelope("idem-1", token: "   "));

        using (Assert.Multiple())
        {
            await Assert.That(result.Accepted).IsFalse()
                .Because("Whitespace-only tokens are invalid input — the handler must reject before touching the slot.");
            await Assert.That(capturingQueue.Enqueued).IsEmpty()
                .Because("Rejected commands must not enqueue work; otherwise a stray validation regression would feed garbage into the worker.");
        }

        await ((IAsyncDisposable)queue).DisposeAsync();
    }

    private static (ImportSpCommandHandler Handler, IImportJobQueue Queue, CapturingQueue Capture) NewHandler()
    {
        var repo = new InMemoryImportOperationRepository();
        var inner = new InProcessImportJobQueue();
        var capture = new CapturingQueue(inner);
        var handler = new ImportSpCommandHandler(repo, capture);
        return (handler, capture, capture);
    }

    private static CommandEnvelope<ImportSpCommand> NewEnvelope(string idempotencyKey, string token) => new(
        OperationId: new("settings:import_sp"),
        CommandId: Guid.NewGuid(),
        PrincipalId: SystemId,
        IdempotencyKey: new(idempotencyKey),
        OccurredAt: DateTimeOffset.UtcNow,
        Payload: new ImportSpCommand(new(token), RecoveryCode: null));

    /// <summary>
    /// Decorator over the real queue so tests can assert on what was enqueued without
    /// re-implementing the channel semantics.
    /// </summary>
    private sealed class CapturingQueue : IImportJobQueue, IAsyncDisposable
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
}
