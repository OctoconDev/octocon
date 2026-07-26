using Interfold.Shared.Domain.Abstractions;
using Interfold.Infrastructure.Coordination;
using Interfold.Infrastructure.InMemory.Repository;
using Interfold.Settings.Api.Services.ImportJobs;
using Interfold.Settings.Contracts.Events;
using Interfold.Settings.Contracts.Ids;
using Interfold.Settings.Contracts.Models.ImportOperations;
using Interfold.Settings.Domain.Abstractions.ImportJobs;
using Interfold.Settings.Domain.Abstractions.Repository;
using Microsoft.Extensions.Logging.Abstractions;
using Interfold.Shared.Contracts.Ids;

namespace Interfold.Api.UnitTests.ImportJobs;

// Lifecycle contract for ImportJobBackgroundService — the only path between a queued
// import job and a terminal repo row + WebSocket lifecycle frame. Wire format for those
// frames is separately pinned by WebSocketTests.
public sealed class ImportJobBackgroundServiceTests
{
    private static readonly ScopedSystemId TestSystemId = ScopedSystemId.ParseScoped("nam:sys-worker-test");

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

    // Thrown exceptions MUST be swallowed so the loop survives; treated identically to
    // a graceful failure with error_code = exception.
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

    [Test]
    public async Task RunAsync_TerminalTransition_ReleasesSlotForNextClaim()
    {
        await using var harness = await Harness.RunAsync(new StubRunner(succeed: true, alterCount: 3));

        var second = await harness.Operations.TryClaimAsync(TestSystemId, ImportOperationKind.SimplyPlural, new("idem-2"));
        await Assert.That(second.IsNew).IsTrue()
            .Because("After the worker terminates an operation, a second click for the same system must claim a fresh slot — otherwise users could never re-import after a successful or failed run.");
    }

    // DI-misregistration guard: an unregistered kind must not pin the slot.
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

        public static async Task<Harness> RunAsync(StubRunner runner, ImportOperationKind? jobKindOverride = null)
        {
            var queue = new InProcessImportJobQueue();
            var operations = new InMemoryImportOperationRepository();
            var bus = new CapturingEventBus();
            var jobKind = jobKindOverride ?? ImportOperationKind.SimplyPlural;

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

            // Unknown-kind path short-circuits without calling RunAsync, so skip the
            // runner gate for those and rely on terminal polling alone.
            if (jobKindOverride is null)
            {
                await runner.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }

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

        // Poll (rather than fixed delay) so slow-CI still gets the terminal snapshot;
        // 5 s hard cap so a hung worker fails visibly.
        private static async Task WaitForTerminalAsync(IImportOperationRepository operations, ImportOperationId operationId)
        {
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
            catch (OperationCanceledException) { }
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
