using Interfold.Contracts.Models.ImportOperations;

namespace Interfold.Api.UnitTests.ImportJobs;

// Async-dispatch contract for ImportSpCommandHandler. Guards against the duplicate-import
// bug class: fresh dispatch enqueues once; concurrent dispatch collapses to Running with
// the same operation_id; empty token rejects without claiming a slot. Shared skeleton is
// ImportDispatchScenario so regressions fail both the SP and PK files at once.
public sealed class ImportSpCommandHandlerDispatchTests
{
    [Test]
    public async Task HandleAsync_FreshDispatch_ReturnsQueuedAndEnqueuesOneItem()
    {
        await using var scenario = ImportDispatchScenario.ForSp();

        var result = await scenario.DispatchAsync();

        using (Assert.Multiple())
        {
            await Assert.That(result.Accepted).IsTrue()
                .Because("A valid token must accept; otherwise the controller would never reach the 202 branch.");
            await Assert.That(result.Result!.Status).IsEqualTo(ImportOperationDispatchStatus.Queued)
                .Because("A fresh claim must surface as 'queued' so the client (and operator logs) can distinguish it from a collapsed dispatch.");
            await Assert.That(scenario.Capture.Enqueued).HasCount(1)
                .Because("Exactly one job must reach the queue per successful claim — more than one would reintroduce the duplicate-import bug class.");
            await Assert.That(scenario.Capture.Enqueued[0].Kind).IsEqualTo(ImportOperationKind.SimplyPlural);
            await Assert.That(scenario.Capture.Enqueued[0].OperationId).IsEqualTo(result.Result!.OperationId)
                .Because("The handler's returned operation_id must match the queued item's id, so the worker writes the row the controller advertised.");
        }
    }

    // Direct guard against the duplicate-SP-import bug class: repeated HTTP attempts
    // (Polly retry, browser retry, double click) produce exactly one worker run.
    [Test]
    public async Task HandleAsync_ConcurrentDispatch_CollapsesOntoSameOperationAndDoesNotReenqueue()
    {
        await using var scenario = ImportDispatchScenario.ForSp();

        var first = await scenario.DispatchAsync("idem-1");
        var second = await scenario.DispatchAsync("idem-2");

        using (Assert.Multiple())
        {
            await Assert.That(second.Accepted).IsTrue();
            await Assert.That(second.Result!.OperationId).IsEqualTo(first.Result!.OperationId)
                .Because("A collapsed dispatch must return the existing operation_id; otherwise the client would subscribe to a frame that never arrives.");
            await Assert.That(second.Result!.Status).IsEqualTo(ImportOperationDispatchStatus.Running)
                .Because("The collapsed status is the agreed signal to the controller that it's a no-op — operator logs and audit trails depend on this string.");
            await Assert.That(scenario.Capture.Enqueued).HasCount(1)
                .Because("Only the original click may enqueue a job; the duplicate dispatch must collapse without producing a second worker run.");
        }
    }

    [Test]
    public async Task HandleAsync_EmptyToken_RejectsWithoutClaimingOrEnqueuing()
    {
        await using var scenario = ImportDispatchScenario.ForSp();

        var result = await scenario.DispatchAsync(token: "   ");

        using (Assert.Multiple())
        {
            await Assert.That(result.Accepted).IsFalse()
                .Because("Whitespace-only tokens are invalid input — the handler must reject before touching the slot.");
            await Assert.That(scenario.Capture.Enqueued).IsEmpty()
                .Because("Rejected commands must not enqueue work; otherwise a stray validation regression would feed garbage into the worker.");
        }
    }
}
