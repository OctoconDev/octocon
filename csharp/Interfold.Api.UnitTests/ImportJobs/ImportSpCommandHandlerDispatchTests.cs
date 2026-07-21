using Interfold.Contracts.Models.ImportOperations;

namespace Interfold.Api.UnitTests.ImportJobs;

/// <summary>
/// Pins the async-dispatch contract of <see cref="Interfold.Domain.Settings.ImportSpCommandHandler"/>.
/// The handler is the choke point that previously ran the entire SP importer synchronously —
/// and inside a Polly-retried POST that produced 2-3× duplicate imports on the Pi 4. These
/// tests pin its new shape:
///
/// <list type="bullet">
///   <item>A successful dispatch claims a slot via the repository and enqueues exactly one job.</item>
///   <item>A concurrent dispatch for the same system observes <c>Status = "running"</c>, returns the same operation_id, and does NOT enqueue a second job (the load-bearing dedupe invariant against the bug class).</item>
///   <item>An empty token is rejected without claiming a slot.</item>
/// </list>
/// The three scenarios drive <see cref="ImportDispatchScenario{TCommand}"/>, whose PK twin
/// runs the same shape — any regression in the shared skeleton fails both files at once.
/// </summary>
public sealed class ImportSpCommandHandlerDispatchTests
{
    /// <summary>
    /// First dispatch for a clean system: handler returns <c>queued</c> and enqueues a job
    /// item carrying the correct kind + token. The operation_id in the result must equal
    /// the queued item's operation_id so the worker and the HTTP caller agree on the
    /// correlation handle.
    /// </summary>
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

    /// <summary>
    /// A second dispatch arriving while the first is still in flight (no terminal yet)
    /// must collapse — same operation_id, no second enqueue. This is the test that
    /// directly guards against the duplicate-SP-import bug class: any number of repeated
    /// HTTP attempts (Polly retry, browser retry, double click) produce exactly one run.
    /// </summary>
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

    /// <summary>
    /// Empty token is invalid input — handler rejects before touching the repository or
    /// the queue. This avoids burning a slot on garbage input and keeps the conflict
    /// shape consistent with the rest of the settings command surface.
    /// </summary>
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
