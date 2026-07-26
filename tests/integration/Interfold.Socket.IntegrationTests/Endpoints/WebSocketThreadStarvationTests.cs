using Interfold.IntegrationTests.Shared;
using Interfold.IntegrationTests.Shared.TestServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Interfold.Socket.IntegrationTests.Endpoints;

/// <summary>
/// Regression guard for the CI-only failure in
/// <c>WebSocketTests.Api_UserSocketEndpoint_PushesFriendTrustedAndUntrusted_ToActor</c>.
///
/// The root cause was <c>SocketEventPumpRunner.SubscribeAsync&lt;TEvent&gt;</c>: a single
/// transient exception inside the per-event <c>await foreach</c> loop tore down that
/// subscription for the rest of the socket's lifetime, so the recipient silently
/// stopped receiving <c>friend_request_received</c> pushes. The bug only reproduced
/// under thread-pool contention (CI runners are ~2 cores; dev boxes are not), which
/// is what makes the underlying handler chain transiently throw.
///
/// This test makes the contention deterministic so the bug stays caught even when
/// nothing else is running. It:
///
/// <list type="number">
///   <item>Runs with <c>[NotInParallel]</c> (no key) so it cannot accidentally rely on
///         other tests warming up the pool.</item>
///   <item>Drops <see cref="ThreadPool.SetMinThreads(int, int)"/> to <c>2 / 2</c> so the
///         runtime injects additional pool threads on its slow ~500ms cadence rather
///         than the eager dev-box default. We deliberately do not touch
///         <see cref="ThreadPool.SetMaxThreads(int, int)"/> — capping max at
///         <see cref="Environment.ProcessorCount"/> gridlocks the test on a many-core
///         box because the synthetic load claims every available thread.</item>
///   <item>Spins up a handful of background tasks that hammer <c>GET /health/live</c>
///         against the same <see cref="Microsoft.AspNetCore.TestHost.TestServer"/> the
///         WebSocket flow is using. Pure CPU hogs (e.g. <see cref="Thread.Sleep(int)"/>
///         loops) weren't enough to reproduce; the CI failure only shows up when the
///         real ASP.NET request pipeline (routing, middleware, DI scope creation, HTTP
///         message handlers) is busy, and hammering a real anonymous endpoint
///         reproduces that contention shape while staying side-effect-free.</item>
///   <item>Runs the exact friend trust/untrust WebSocket flow that fails in CI.</item>
///   <item>Cancels the load tasks and restores the previous min in <c>finally</c> so
///         the rest of the session is unaffected.</item>
/// </list>
///
/// If the resilience guard in <c>SocketEventPumpRunner</c> is removed (or another
/// regression introduces the same "one handler throw kills the subscription"
/// behaviour) this test fails locally and in CI without having to rely on the
/// natural thread-pool pressure of a parallel suite.
///
/// <c>[Retry(2)]</c> is intentional: this is a stress test driven by synthetic load,
/// so on a quiet pool we want it to assert hard, but on a saturated host (low-core
/// CI runner with other concurrent work, busy dev box) a single timing slip past the
/// 30s per-frame ReceivedPhxFrame budget should not turn the build red. Pre-fix the
/// failure is deterministic — every attempt fails — so retries don't mask the
/// regression we're guarding against; post-fix flakiness collapses to a small
/// residual that the retries absorb.
/// </summary>
[Timeout(1000 * 300)]
[NotInParallel]
[Retry(2)]
[ClassDataSource<InMemoryWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
[ClassDataSource<CassandraWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public class WebSocketThreadStarvationTests(IWebFactoryFixture fixture) : BaseEndpointTest
{
    // Match the CI runner's pre-warmed worker/IO counts. SetMaxThreads cannot go below
    // ProcessorCount on .NET, so we additionally saturate the pool with synthetic
    // hammer tasks below to make it _behave_ like a small pool on any host.
    private const int ThrottledWorkerMin = 2;
    private const int ThrottledIoMin = 2;

    [Test]
    public async Task Api_UserSocketEndpoint_PushesFriendTrustedAndUntrusted_ToActor_UnderThreadStarvation(CancellationToken token)
    {
        var logger = fixture.Factory.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger<WebSocketThreadStarvationTests>();

        ThreadPool.GetMinThreads(out var previousWorkerMin, out var previousIoMin);

        // Lower MIN only — capping MAX gridlocks the test on dev boxes (the synthetic
        // load claims every available thread and the WebSocket continuations have
        // nowhere to run). With MIN lowered the runtime injects new threads on its
        // slow ~500ms cadence while the hammer keeps the request pipeline busy.
        var minApplied = ThreadPool.SetMinThreads(ThrottledWorkerMin, ThrottledIoMin);

        var processorCount = Environment.ProcessorCount;

        // Hammer count scales linearly with cores so the test produces consistent
        // pipeline pressure on any host — not just the 2-core CI runner — and the
        // bug reproduces deterministically without the fix on dev boxes too.
        //
        //   - 2-core CI runner  -> 4 tasks (floor)
        //   - 14-core dev box   -> 7 tasks
        //   - 32-core CI worker -> 16 tasks
        //   - 64-core monster   -> 32 tasks
        //
        // Floor of 4 matches the original clamp that reliably reproduces pre-fix on
        // the smallest CI runner. We deliberately don't cap on the high end any more
        // — the previous Math.Clamp(., 4, 4) ceiling meant a 64-core host saw the
        // same 4 hammers as a 16-core, so the regression couldn't reproduce there at
        // all. Combined with the 25ms per-request breather (~40 req/s per task) that
        // works out to ~160 req/s on CI scaling to ~1.3k req/s on a 64-core box —
        // enough pipeline contention everywhere without OOMing the InMemory fixture
        // at any size. [Retry(2)] on the class absorbs any post-fix timing slips
        // that the larger total throughput introduces; pre-fix the failure is still
        // deterministic on every attempt.
        var hammerCount = Math.Max(4, processorCount / 2);

        logger.LogInformation(
            "ThreadPool throttle applied. minApplied={MinApplied} processors={Processors} hammerTasks={HammerTasks} " +
            "prevMin=({PrevWorkerMin},{PrevIoMin}) newMin=({NewWorkerMin},{NewIoMin})",
            minApplied,
            processorCount,
            hammerCount,
            previousWorkerMin,
            previousIoMin,
            ThrottledWorkerMin,
            ThrottledIoMin);

        using var stopHammer = new CancellationTokenSource();
        var hammerTasks = new List<Task>(hammerCount);
        for (var i = 0; i < hammerCount; i++)
        {
            hammerTasks.Add(Task.Run(async () =>
            {
                // Each hammer task uses its own HttpClient so we don't contend on a
                // single client's connection pool — we want contention on the
                // TestServer pipeline (routing, middleware, DI scoping, HttpClient
                // continuations), not on a single client's outbound queue.
                using var hammerClient = fixture.Factory.CreateClient();

                while (!stopHammer.Token.IsCancellationRequested)
                {
                    try
                    {
                        // ResponseHeadersRead avoids buffering the response body.
                        // /health/live returns only ~30 bytes, but at unthrottled
                        // throughput the buffering OOMed the InMemory fixture in
                        // under a minute on dev.
                        using (await hammerClient.GetAsync(
                            "/health/live",
                            HttpCompletionOption.ResponseHeadersRead,
                            stopHammer.Token))
                        {
                            // No-op: response disposes when the using exits.
                        }

                        // ~40 RPS per task. The goal is sustained pipeline pressure
                        // (so the runtime keeps queuing continuations while the
                        // friend-request flow runs), not a load test.
                        await Task.Delay(25, stopHammer.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch
                    {
                        // Transient failures are fine; the point is keeping the
                        // request pipeline busy, not asserting health-check success.
                    }
                }
            }));
        }

        try
        {
            await RunFriendTrustUntrustFlowAsync(token);
        }
        finally
        {
            await stopHammer.CancelAsync();
            try { await Task.WhenAll(hammerTasks); } catch { /* cancellation throws are expected */ }
            ThreadPool.SetMinThreads(previousWorkerMin, previousIoMin);
            logger.LogInformation(
                "ThreadPool throttle released. hammerTasks={HammerTasks} restoredMin=({RestoredWorkerMin},{RestoredIoMin})",
                hammerCount,
                previousWorkerMin,
                previousIoMin);
        }
    }

    private async Task RunFriendTrustUntrustFlowAsync(CancellationToken token)
    {
        var senderSystemId = WebSocketTests.UniqueId("sys-starve-sender");
        var recipientSystemId = WebSocketTests.UniqueId("sys-starve-recipient");

        var pair = await WebSocketHarness.ConnectPairAndJoinAsync(fixture, senderSystemId, recipientSystemId, token);
        using var senderWs = pair.FirstWs;
        using var recipientWs = pair.SecondWs;

        await FriendTrustUntrustFlow.RunAsync(
            senderWs, senderSystemId, recipientWs, recipientSystemId, token,
            contextSuffix: "under thread starvation");

        await WebSocketExtensions.CloseTestDoneAsync(senderWs, recipientWs, token);
    }
}
