using Interfold.Api.Services.ImportJobs;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models.ImportOperations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.ImportJobs;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Infrastructure.Coordination;
using Interfold.Infrastructure.InMemory.Repository;
using Microsoft.Extensions.Logging.Abstractions;
using Interfold.Contracts.Ids;

namespace Interfold.Api.UnitTests.ImportJobs;

/// <summary>
/// Pins the lifecycle contract of <see cref="ImportJobBackgroundService"/>. The worker is
/// the only path between a queued import job and a terminal Cassandra row + WebSocket
/// frame, so any regression here directly breaks the user-visible import experience.
///
/// <para>
/// These tests run the real worker against the in-memory queue and repository so the
/// behaviour under test is end-to-end-correct. The only fakes are <see cref="StubRunner"/>
/// (so we can deterministically simulate success / graceful failure / thrown exception)
/// and <see cref="CapturingEventBus"/> (so we can assert which lifecycle events were
/// published). The wire format for those events is separately pinned by
/// <c>WebSocketTests.Api_UserSocketEndpoint_PushesSpImportLifecycleEvents_OnEventBusPublish</c>
/// — together those two tests cover the full publish-and-project pipeline.
/// </para>
/// </summary>
public sealed class ImportJobBackgroundServiceTests
{
    private static readonly ScopedSystemId TestSystemId = ScopedSystemId.ParseScoped("nam:sys-worker-test");

    /// <summary>
    /// Happy path: the runner reports success, the repository row transitions to
    /// <see cref="ImportOperationStatus.Succeeded"/> with the reported alter count, and
    /// the success event is published. This is the contract the Compose app depends on
    /// for the "ImportStatus.Success(it.alterCount)" branch in <c>SettingsRootScreen.kt</c>.
    /// </summary>
    [Test]
    public async Task RunAsync_RunnerSucceeds_MarksSucceededAndPublishesCompleteEvent()
    {
        await using var harness = await Harness.RunAsync(new StubRunner(succeed: true, alterCount: 7));

        var snapshot = await harness.GetOperationAsync();
        using (Assert.Multiple())
        {
            await Assert.That(snapshot!.Status).IsEqualTo(ImportOperationStatus.Succeeded)
                .Because("A successful runner outcome must produce a terminal Succeeded row; otherwise the per-system slot stays locked and re-imports become impossible.");
            await Assert.That(snapshot.AlterCount).IsEqualTo(7)
                .Because("The alter count from the runner must be persisted verbatim — the WebSocket frame reads it from the row publish via the worker's MarkSucceeded call.");
            await Assert.That(harness.EventBus.Published).Contains(e => e is SimplyPluralImportCompletedEvent c && c.AlterCount == 7)
                .Because("Exactly one SimplyPluralImportCompletedEvent must be published with the reported alter count; that frame drives the client's Success transition.");
            await Assert.That(harness.EventBus.Published).Contains(e => e is SettingsProfileUpdatedEvent)
                .Because("SP imports also fire a SettingsProfileUpdated event so dependent client views (encryption status pill, etc.) refresh.");
        }
    }

    /// <summary>
    /// Graceful failure path: the runner returns <c>Success = false</c> with a code. The
    /// worker must record that, free the slot, and publish the failure event. Compose
    /// uses this to render <c>ImportStatus.Failed</c>.
    /// </summary>
    [Test]
    public async Task RunAsync_RunnerReportsGracefulFailure_MarksFailedAndPublishesFailedEvent()
    {
        await using var harness = await Harness.RunAsync(new StubRunner(succeed: false, errorCode: ImportErrorCode.SpAuthFailed));

        var snapshot = await harness.GetOperationAsync();
        using (Assert.Multiple())
        {
            await Assert.That(snapshot!.Status).IsEqualTo(ImportOperationStatus.Failed)
                .Because("A graceful (Success=false) outcome must still terminate the row in Failed so the LWT slot frees.");
            await Assert.That(snapshot.ErrorCode).IsEqualTo(ImportErrorCode.SpAuthFailed)
                .Because("Runner-supplied error codes must round-trip to the row so operators can grep the failure category.");
            await Assert.That(harness.EventBus.Published).Contains(e => e is SimplyPluralImportFailedEvent)
                .Because("A failed import must publish SimplyPluralImportFailedEvent so the client flips out of Importing.");
        }
    }

    /// <summary>
    /// Thrown-exception path: the runner throws. The worker MUST swallow the exception
    /// (so the loop survives for the next job) and treat it identically to a graceful
    /// failure with <c>error_code = "exception"</c>.
    /// </summary>
    [Test]
    public async Task RunAsync_RunnerThrows_MarksFailedWithExceptionCodeAndPublishesFailedEvent()
    {
        await using var harness = await Harness.RunAsync(new StubRunner(throws: new InvalidOperationException("boom")));

        var snapshot = await harness.GetOperationAsync();
        using (Assert.Multiple())
        {
            await Assert.That(snapshot!.Status).IsEqualTo(ImportOperationStatus.Failed)
                .Because("A thrown exception must NOT leave the row pinned at Running — that would deadlock the per-system slot until the next host restart sweep.");
            await Assert.That(snapshot.ErrorCode).IsEqualTo(ImportErrorCode.Exception)
                .Because("Thrown exceptions are categorised as 'exception' so the audit trail distinguishes them from runner-reported graceful failures.");
            await Assert.That(harness.EventBus.Published).Contains(e => e is SimplyPluralImportFailedEvent)
                .Because("Even on a thrown exception, the client must receive a failure frame — otherwise the dialog stays on Importing forever.");
        }
    }

    /// <summary>
    /// Releasing the LWT slot on terminal transitions is what lets the user click again.
    /// This test combines the contract of <see cref="IImportOperationRepository"/> and
    /// the worker: after the worker drives a job to Succeeded, the repository must
    /// accept a fresh claim for the same system.
    /// </summary>
    [Test]
    public async Task RunAsync_TerminalTransition_ReleasesSlotForNextClaim()
    {
        await using var harness = await Harness.RunAsync(new StubRunner(succeed: true, alterCount: 3));

        var second = await harness.Operations.TryClaimAsync(TestSystemId, ImportOperationKind.SimplyPlural, new("idem-2"));
        await Assert.That(second.IsNew).IsTrue()
            .Because("After the worker terminates an operation, a second click for the same system must claim a fresh slot — otherwise users could never re-import after a successful or failed run.");
    }

    /// <summary>
    /// Unregistered-kind path: a job whose kind has no registered runner must not pin
    /// the slot. The worker fails it cleanly with <c>error_code = "no_runner"</c> and
    /// the queue keeps consuming. Uses PluralKit as the item's kind while registering
    /// only the SimplyPlural runner — the strong-typed enum enforces this scenario at
    /// the type layer, but the DI-misregistration guard still needs coverage in case a
    /// future runner ships broken.
    /// </summary>
    [Test]
    public async Task RunAsync_UnknownKind_MarksFailedWithNoRunnerCode()
    {
        await using var harness = await Harness.RunAsync(
            runner: new StubRunner(succeed: true),
            jobKindOverride: ImportOperationKind.PluralKit);

        var snapshot = await harness.GetOperationAsync();
        await Assert.That(snapshot!.Status).IsEqualTo(ImportOperationStatus.Failed)
            .Because("If no runner is registered for the requested kind the worker must fail the row rather than leave it queued indefinitely.");
        await Assert.That(snapshot.ErrorCode).IsEqualTo(ImportErrorCode.NoRunner)
            .Because("The 'no_runner' code is the agreed signal for a DI misregistration; operators grep on it to alert on missing platform integrations.");
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required InProcessImportJobQueue Queue { get; init; }
        public required IImportOperationRepository Operations { get; init; }
        public required CapturingEventBus EventBus { get; init; }
        public required ImportJobBackgroundService Worker { get; init; }
        public required ImportOperationId OperationId { get; init; }
        public required CancellationTokenSource Cts { get; init; }

        public Task<ImportOperationSnapshot?> GetOperationAsync() =>
            Operations.GetByIdAsync(TestSystemId, OperationId);

        /// <summary>
        /// Spins up a worker against a single-item queue, waits for the runner to
        /// observe the job (or a timeout), then stops the worker. The returned harness
        /// holds the resulting repository state for assertions.
        /// </summary>
        public static async Task<Harness> RunAsync(StubRunner runner, ImportOperationKind? jobKindOverride = null)
        {
            var queue = new InProcessImportJobQueue();
            var operations = new InMemoryImportOperationRepository();
            var bus = new CapturingEventBus();
            var jobKind = jobKindOverride ?? ImportOperationKind.SimplyPlural;

            // Pre-claim the slot the way the real handler would, so the worker has a
            // legitimate row to transition.
            var claim = await operations.TryClaimAsync(TestSystemId, ImportOperationKind.SimplyPlural, new("idem-1"));
            var item = new ImportJobItem(claim.OperationId, TestSystemId, jobKind, Token: new("synthetic"), RecoveryCode: null);

            var cts = new CancellationTokenSource();
            var worker = new ImportJobBackgroundService(
                queue,
                operations,
                bus,
                runners: new IImportJobRunner[] { runner },
                NullLogger<ImportJobBackgroundService>.Instance);

            await worker.StartAsync(cts.Token);
            await queue.EnqueueAsync(item, cts.Token);

            // For known kinds, wait for the runner to actually see the job. For unknown
            // kinds the worker short-circuits without calling RunAsync, so we skip the
            // runner gate and rely on terminal polling alone.
            if (jobKindOverride is null)
            {
                await runner.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }

            // Give the worker a brief moment to finish writing the terminal row +
            // publishing the event after RunAsync returned (or after the kind-not-found
            // short-circuit).
            await WaitForTerminalAsync(operations, claim.OperationId);

            return new Harness
            {
                Queue = queue,
                Operations = operations,
                EventBus = bus,
                Worker = worker,
                OperationId = claim.OperationId,
                Cts = cts,
            };
        }

        private static async Task WaitForTerminalAsync(IImportOperationRepository operations, ImportOperationId operationId)
        {
            // Poll instead of relying on a fixed delay so a slow-CI iteration still gets
            // the terminal snapshot rather than a Running one. Hard cap at 5s to fail
            // visibly if the worker hangs.
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var snapshot = await operations.GetByIdAsync(TestSystemId, operationId);
                if (snapshot?.Status is ImportOperationStatus.Succeeded or ImportOperationStatus.Failed)
                {
                    return;
                }
                await Task.Delay(25);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            try { await Worker.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { /* expected on shutdown */ }
            await Queue.DisposeAsync();
            Cts.Dispose();
        }
    }

    private sealed class StubRunner : IImportJobRunner
    {
        private readonly bool _succeed;
        private readonly int _alterCount;
        private readonly ImportErrorCode? _errorCode;
        private readonly Exception? _throws;

        public TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StubRunner(bool succeed = true, int alterCount = 0, ImportErrorCode? errorCode = null, Exception? throws = null)
        {
            _succeed = succeed;
            _alterCount = alterCount;
            _errorCode = errorCode;
            _throws = throws;
        }

        public ImportOperationKind Kind => ImportOperationKind.SimplyPlural;

        public Task<ImportJobOutcome> RunAsync(ImportJobItem item, CancellationToken cancellationToken = default)
        {
            Observed.TrySetResult();
            if (_throws is not null) throw _throws;
            return Task.FromResult(_succeed
                ? new ImportJobOutcome(Success: true, AlterCount: _alterCount)
                : new ImportJobOutcome(Success: false, AlterCount: 0, ErrorCode: _errorCode));
        }
    }

    private sealed class CapturingEventBus : IClusterEventBus
    {
        private readonly List<object> _published = new();
        private readonly object _gate = new();

        public IReadOnlyList<object> Published
        {
            get { lock (_gate) return _published.ToArray(); }
        }

        public ValueTask PublishAsync<TEvent>(TEvent evt, CancellationToken ct = default) where TEvent : class
        {
            lock (_gate) _published.Add(evt);
            return ValueTask.CompletedTask;
        }

        public IAsyncEnumerable<TEvent> SubscribeAsync<TEvent>(ScopedSystemId? targetSystemId, CancellationToken ct = default)
            where TEvent : class => EmptyAsync<TEvent>();

        private static async IAsyncEnumerable<T> EmptyAsync<T>()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
