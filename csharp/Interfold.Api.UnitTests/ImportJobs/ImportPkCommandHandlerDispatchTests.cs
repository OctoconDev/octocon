using Interfold.Contracts.Models.ImportOperations;
using Interfold.Infrastructure.Coordination;
using Interfold.Infrastructure.InMemory.Repository;

namespace Interfold.Api.UnitTests.ImportJobs;

/// <summary>
/// Pins the async-dispatch contract of <see cref="Interfold.Domain.Settings.ImportPkCommandHandler"/>.
/// PK is the stub-importer twin of the SP path — but the dispatch shape is identical, and
/// re-asserting it here means a future regression in either handler stays loud rather than
/// silently breaking the other platform's lifecycle. Shared skeleton lives in
/// <see cref="ImportDispatchScenario{TCommand}"/>; per-kind extras (recovery-code contract
/// leak guard, cross-kind concurrency invariant) stay inline here where they belong.
/// </summary>
public sealed class ImportPkCommandHandlerDispatchTests
{
    /// <summary>
    /// PK dispatch under the same shape as SP: fresh claim, queued result, exactly one
    /// enqueued item carrying the PK kind. Also pins that PK does NOT carry a recovery
    /// code through to the queue item (PK's auth model is token-only).
    /// </summary>
    [Test]
    public async Task HandleAsync_FreshDispatch_ReturnsQueuedAndEnqueuesOnePkItem()
    {
        await using var scenario = ImportDispatchScenario.ForPk();

        var result = await scenario.DispatchAsync(token: "synthetic-pk-token");

        using (Assert.Multiple())
        {
            await Assert.That(result.Accepted).IsTrue();
            await Assert.That(result.Result!.Status).IsEqualTo(ImportOperationDispatchStatus.Queued);
            await Assert.That(result.Result!.Kind).IsEqualTo(ImportOperationKind.PluralKit)
                .Because("PK dispatches must surface the 'pk' kind so the worker routes to the PK runner — wiring it to 'sp' would silently run SP code against a PK token.");
            await Assert.That(scenario.Capture.Enqueued).HasCount(1);
            await Assert.That(scenario.Capture.Enqueued[0].RecoveryCode).IsNull()
                .Because("PK has no recovery-code concept; carrying one through would be a contract leak from the SP path.");
        }
    }

    /// <summary>
    /// Same per-system collapse story as SP, but for the PK kind. Pins that the LWT
    /// mutex partitions on (system, kind) — a PK dispatch that lands while another PK
    /// is in flight collapses correctly.
    /// </summary>
    [Test]
    public async Task HandleAsync_ConcurrentPkDispatch_CollapsesOntoSameOperation()
    {
        await using var scenario = ImportDispatchScenario.ForPk();

        var first = await scenario.DispatchAsync("idem-1", token: "synthetic-pk-token");
        var second = await scenario.DispatchAsync("idem-2", token: "synthetic-pk-token");

        using (Assert.Multiple())
        {
            await Assert.That(second.Result!.OperationId).IsEqualTo(first.Result!.OperationId)
                .Because("Repeated PK dispatches for the same system must collapse onto the in-flight op — same guarantee the SP handler gives.");
            await Assert.That(second.Result!.Status).IsEqualTo(ImportOperationDispatchStatus.Running);
            await Assert.That(scenario.Capture.Enqueued).HasCount(1)
                .Because("Only one PK worker run per active claim — duplicate enqueues would re-introduce the bug class on the PK side once the real importer lands.");
        }
    }

    /// <summary>
    /// SP and PK for the same system run independently — the LWT mutex partitions on
    /// (system, kind), not just system. Pins that a user can SP-import and PK-import
    /// concurrently without artificial serialisation. Kept inline (not on the shared
    /// scenario) because it uniquely needs two handlers sharing one repository.
    /// </summary>
    [Test]
    public async Task HandleAsync_SpAndPk_SameSystem_BothClaimDistinctSlots()
    {
        var repo = new InMemoryImportOperationRepository();
        var queue = new InProcessImportJobQueue();
        var capture = new CapturingQueue(queue);
        var sp = new Interfold.Domain.Settings.ImportSpCommandHandler(repo, capture);
        var pk = new Interfold.Domain.Settings.ImportPkCommandHandler(repo, capture);

        var spResult = await sp.HandleAsync(
            ImportDispatchScenario.NewSpEnvelope(ImportDispatchScenario.PkSystemId, "idem-sp", "synthetic-sp-token"));
        var pkResult = await pk.HandleAsync(
            ImportDispatchScenario.NewPkEnvelope(ImportDispatchScenario.PkSystemId, "idem-pk", "synthetic-pk-token"));

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

        await queue.DisposeAsync();
    }

    /// <summary>
    /// Empty token is rejected without touching the repository or queue, mirroring the
    /// SP handler. Pins that input validation lives at the same layer for both
    /// platforms.
    /// </summary>
    [Test]
    public async Task HandleAsync_EmptyToken_RejectsWithoutClaimingOrEnqueuing()
    {
        await using var scenario = ImportDispatchScenario.ForPk();

        var result = await scenario.DispatchAsync(token: string.Empty);

        await Assert.That(result.Accepted).IsFalse()
            .Because("PK dispatches with no token are invalid input — the handler must reject before claiming a slot.");
        await Assert.That(scenario.Capture.Enqueued).IsEmpty();
    }
}
