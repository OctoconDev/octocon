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
/// Pins the async-dispatch contract of <see cref="ImportPkCommandHandler"/>. PK is the
/// stub-importer twin of the SP path — but the dispatch shape is identical, and
/// re-asserting it here means a future regression in either handler stays loud rather
/// than silently breaking the other platform's lifecycle.
/// </summary>
public sealed class ImportPkCommandHandlerDispatchTests
{
    // PrincipalId is a ScopedSystemId; ParseScoped enforces the wire-canonical form so
    // the test fixture matches what the middleware constructs at ingress.
    private static readonly ScopedSystemId SystemId
        = ScopedSystemId.ParseScoped("nam:sys-pk-dispatch-test");

    /// <summary>
    /// PK dispatch under the same shape as SP: fresh claim, queued result, exactly one
    /// enqueued item carrying the PK kind. Also pins that PK does NOT carry a recovery
    /// code through to the queue item (PK's auth model is token-only).
    /// </summary>
    [Test]
    public async Task HandleAsync_FreshDispatch_ReturnsQueuedAndEnqueuesOnePkItem()
    {
        var (handler, queue, capturingQueue) = NewHandler();

        var result = await handler.HandleAsync(NewEnvelope("idem-1", token: "synthetic-pk-token"));

        using (Assert.Multiple())
        {
            await Assert.That(result.Accepted).IsTrue();
            await Assert.That(result.Result!.Status).IsEqualTo(ImportOperationDispatchStatus.Queued);
            await Assert.That(result.Result!.Kind).IsEqualTo(ImportOperationKind.PluralKit)
                .Because("PK dispatches must surface the 'pk' kind so the worker routes to the PK runner — wiring it to 'sp' would silently run SP code against a PK token.");
            await Assert.That(capturingQueue.Enqueued).HasCount(1);
            await Assert.That(capturingQueue.Enqueued[0].RecoveryCode).IsNull()
                .Because("PK has no recovery-code concept; carrying one through would be a contract leak from the SP path.");
        }

        await ((IAsyncDisposable)queue).DisposeAsync();
    }

    /// <summary>
    /// Same per-system collapse story as SP, but for the PK kind. Pins that the LWT
    /// mutex partitions on (system, kind) — a PK dispatch that lands while another PK
    /// is in flight collapses correctly.
    /// </summary>
    [Test]
    public async Task HandleAsync_ConcurrentPkDispatch_CollapsesOntoSameOperation()
    {
        var (handler, queue, capturingQueue) = NewHandler();

        var first = await handler.HandleAsync(NewEnvelope("idem-1", token: "synthetic-pk-token"));
        var second = await handler.HandleAsync(NewEnvelope("idem-2", token: "synthetic-pk-token"));

        using (Assert.Multiple())
        {
            await Assert.That(second.Result!.OperationId).IsEqualTo(first.Result!.OperationId)
                .Because("Repeated PK dispatches for the same system must collapse onto the in-flight op — same guarantee the SP handler gives.");
            await Assert.That(second.Result!.Status).IsEqualTo(ImportOperationDispatchStatus.Running);
            await Assert.That(capturingQueue.Enqueued).HasCount(1)
                .Because("Only one PK worker run per active claim — duplicate enqueues would re-introduce the bug class on the PK side once the real importer lands.");
        }

        await ((IAsyncDisposable)queue).DisposeAsync();
    }

    /// <summary>
    /// SP and PK for the same system run independently — the LWT mutex partitions on
    /// (system, kind), not just system. Pins that a user can SP-import and PK-import
    /// concurrently without artificial serialisation.
    /// </summary>
    [Test]
    public async Task HandleAsync_SpAndPk_SameSystem_BothClaimDistinctSlots()
    {
        var repo = new InMemoryImportOperationRepository();
        var queue = new InProcessImportJobQueue();
        var capture = new CapturingQueue(queue);
        var sp = new ImportSpCommandHandler(repo, capture);
        var pk = new ImportPkCommandHandler(repo, capture);

        var spResult = await sp.HandleAsync(NewSpEnvelope("idem-sp"));
        var pkResult = await pk.HandleAsync(NewEnvelope("idem-pk", token: "synthetic-pk-token"));

        using (Assert.Multiple())
        {
            await Assert.That(spResult.Result!.Status).IsEqualTo(ImportOperationDispatchStatus.Queued);
            await Assert.That(pkResult.Result!.Status).IsEqualTo(ImportOperationDispatchStatus.Queued)
                .Because("A PK dispatch must not be blocked by an in-flight SP dispatch for the same system — the mutex is per (system, kind).");
            await Assert.That(spResult.Result!.OperationId).IsNotEqualTo(pkResult.Result!.OperationId)
                .Because("Distinct kinds must get distinct operation_ids; aliasing them would corrupt the audit trail.");
            await Assert.That(capture.Enqueued).HasCount(2)
                .Because("Both dispatches must produce their own worker run — one per kind.");
        }

        await ((IAsyncDisposable)queue).DisposeAsync();
    }

    /// <summary>
    /// Empty token is rejected without touching the repository or queue, mirroring the
    /// SP handler. Pins that input validation lives at the same layer for both
    /// platforms.
    /// </summary>
    [Test]
    public async Task HandleAsync_EmptyToken_RejectsWithoutClaimingOrEnqueuing()
    {
        var (handler, queue, capturingQueue) = NewHandler();

        var result = await handler.HandleAsync(NewEnvelope("idem-1", token: string.Empty));

        await Assert.That(result.Accepted).IsFalse()
            .Because("PK dispatches with no token are invalid input — the handler must reject before claiming a slot.");
        await Assert.That(capturingQueue.Enqueued).IsEmpty();

        await ((IAsyncDisposable)queue).DisposeAsync();
    }

    private static (ImportPkCommandHandler Handler, IImportJobQueue Queue, CapturingQueue Capture) NewHandler()
    {
        var repo = new InMemoryImportOperationRepository();
        var inner = new InProcessImportJobQueue();
        var capture = new CapturingQueue(inner);
        var handler = new ImportPkCommandHandler(repo, capture);
        return (handler, capture, capture);
    }

    private static CommandEnvelope<ImportPkCommand> NewEnvelope(string idempotencyKey, string token) => new(
        OperationId: new("settings:import_pk"),
        CommandId: Guid.NewGuid(),
        PrincipalId: SystemId,
        IdempotencyKey: new(idempotencyKey),
        OccurredAt: DateTimeOffset.UtcNow,
        Payload: new ImportPkCommand(new(token)));

    private static CommandEnvelope<ImportSpCommand> NewSpEnvelope(string idempotencyKey) => new(
        OperationId: new("settings:import_sp"),
        CommandId: Guid.NewGuid(),
        PrincipalId: SystemId,
        IdempotencyKey: new(idempotencyKey),
        OccurredAt: DateTimeOffset.UtcNow,
        Payload: new ImportSpCommand(new("synthetic-sp-token"), RecoveryCode: null));

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
