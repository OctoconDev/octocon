using Interfold.Contracts.Models.ImportOperations;
using Interfold.Infrastructure.InMemory.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Api.UnitTests.ImportJobs;

/// <summary>
/// Pins the per-system mutex contract of the in-memory <c>IImportOperationRepository</c>.
/// The InMemory port has to behave identically to the Scylla port for the integration
/// tests (which run against InMemory by default) to be a meaningful proxy for the
/// production Cassandra LWT semantics. If these tests pass, both ports give us the
/// invariant that protects against the duplicate-SP-import bug: <b>at most one
/// queued-or-running operation per <c>(system, kind)</c> pair</b>.
/// </summary>
public sealed class InMemoryImportOperationRepositoryTests
{
    /// <summary>
    /// A fresh claim against an empty store returns <c>IsNew = true</c> and a non-empty
    /// operation_id. Establishes the baseline so the subsequent collision tests have a
    /// reference shape.
    /// </summary>
    [Test]
    public async Task TryClaim_NoActiveOperation_ReturnsIsNewTrue()
    {
        var repo = new InMemoryImportOperationRepository();

        var claim = await repo.TryClaimAsync(new("nam:sys-a"), ImportOperationKind.SimplyPlural, new("idem-1"));

        using (Assert.Multiple())
        {
            await Assert.That(claim.IsNew).IsTrue()
                .Because("The first claim against an empty store must always succeed; otherwise the dispatcher has no way to start an import.");
            await Assert.That(claim.OperationId).IsNotEqualTo(ImportOperationId.Empty)
                .Because("A successful claim must return a real operation_id — the controller uses this value as the public correlation handle.");
        }
    }

    /// <summary>
    /// Two sequential claims for the same system+kind: the first succeeds, the second
    /// collapses onto it (same operation_id, <c>IsNew = false</c>). This is the load-bearing
    /// invariant; if it ever fails, two simultaneous dispatches will each enqueue their
    /// own worker run and reproduce the duplicate-import bug.
    /// </summary>
    [Test]
    public async Task TryClaim_WhileActive_CollapsesOntoExistingOperation()
    {
        var repo = new InMemoryImportOperationRepository();
        var first = await repo.TryClaimAsync(new("nam:sys-a"), ImportOperationKind.SimplyPlural, new("idem-1"));

        var second = await repo.TryClaimAsync(new("nam:sys-a"), ImportOperationKind.SimplyPlural, new("idem-2"));

        using (Assert.Multiple())
        {
            await Assert.That(second.IsNew).IsFalse()
                .Because("A claim while the slot is already taken must collapse — IsNew=true here means the dispatcher would enqueue a second worker run.");
            await Assert.That(second.OperationId).IsEqualTo(first.OperationId)
                .Because("The collapsed claim must return the same operation_id so the caller subscribes to the same WebSocket completion frame.");
        }
    }

    /// <summary>
    /// Different systems do NOT collide — system A's in-flight import must not block
    /// system B from dispatching. Pins that the mutex key includes system_id, not just
    /// kind.
    /// </summary>
    [Test]
    public async Task TryClaim_DifferentSystems_BothSucceed()
    {
        var repo = new InMemoryImportOperationRepository();
        var claimA = await repo.TryClaimAsync(new("nam:sys-a"), ImportOperationKind.SimplyPlural, new("idem-1"));

        var claimB = await repo.TryClaimAsync(new("nam:sys-b"), ImportOperationKind.SimplyPlural, new("idem-2"));

        using (Assert.Multiple())
        {
            await Assert.That(claimA.IsNew).IsTrue();
            await Assert.That(claimB.IsNew).IsTrue()
                .Because("System B's import must not be blocked by system A's — the mutex partition is per (system, kind).");
            await Assert.That(claimB.OperationId).IsNotEqualTo(claimA.OperationId)
                .Because("Each fresh claim must mint its own operation_id; sharing ids across systems would break the audit trail.");
        }
    }

    /// <summary>
    /// SP and PK for the same system run independently — kind is part of the mutex key.
    /// Pins that future cross-platform users (importing SP and PK in parallel for the
    /// same account) aren't artificially serialised.
    /// </summary>
    [Test]
    public async Task TryClaim_SameSystemDifferentKinds_BothSucceed()
    {
        var repo = new InMemoryImportOperationRepository();
        var spClaim = await repo.TryClaimAsync(new("nam:sys-a"), ImportOperationKind.SimplyPlural, new("idem-1"));

        var pkClaim = await repo.TryClaimAsync(new("nam:sys-a"), ImportOperationKind.PluralKit, new("idem-2"));

        using (Assert.Multiple())
        {
            await Assert.That(spClaim.IsNew).IsTrue();
            await Assert.That(pkClaim.IsNew).IsTrue()
                .Because("SP and PK imports are independent operations; they must not share a mutex.");
        }
    }

    /// <summary>
    /// Once the slot is released via a terminal transition (MarkSucceeded /
    /// MarkFailed), the same system can dispatch again. Combined with the
    /// previous tests this gives the full lifecycle: claim → terminal → re-claim.
    /// </summary>
    [Test]
    public async Task TryClaim_AfterSucceeded_ReleasesSlotAndAllowsReclaim()
    {
        var repo = new InMemoryImportOperationRepository();
        var first = await repo.TryClaimAsync(new("nam:sys-a"), ImportOperationKind.SimplyPlural, new("idem-1"));
        await repo.MarkSucceededAsync(new("nam:sys-a"), first.OperationId, ImportOperationKind.SimplyPlural, alterCount: 5);

        var second = await repo.TryClaimAsync(new("nam:sys-a"), ImportOperationKind.SimplyPlural, new("idem-2"));

        using (Assert.Multiple())
        {
            await Assert.That(second.IsNew).IsTrue()
                .Because("After the terminal transition the per-system slot must be free; otherwise users could never re-import after success.");
            await Assert.That(second.OperationId).IsNotEqualTo(first.OperationId)
                .Because("The new claim must mint a fresh operation_id — reusing the old one would alias two distinct imports in the audit log.");
        }
    }

    /// <summary>
    /// Same shape as the succeeded case but for the failed terminal. Re-claim must
    /// still work — a user who got a failure frame must be able to retry without
    /// waiting for the sweep.
    /// </summary>
    [Test]
    public async Task TryClaim_AfterFailed_ReleasesSlotAndAllowsReclaim()
    {
        var repo = new InMemoryImportOperationRepository();
        var first = await repo.TryClaimAsync(new("nam:sys-a"), ImportOperationKind.SimplyPlural, new("idem-1"));
        await repo.MarkFailedAsync(new("nam:sys-a"), first.OperationId, ImportOperationKind.SimplyPlural, ImportErrorCode.ImportFailed, "synthetic");

        var second = await repo.TryClaimAsync(new("nam:sys-a"), ImportOperationKind.SimplyPlural, new("idem-2"));

        await Assert.That(second.IsNew).IsTrue()
            .Because("MarkFailed must free the slot exactly like MarkSucceeded — both are terminal transitions.");
    }

    /// <summary>
    /// Parallel claims: 16 concurrent tasks racing for the same slot must converge on
    /// exactly one winner. This is the strongest test of the mutex — if the in-memory
    /// TryAdd were not actually atomic, multiple tasks would observe IsNew=true and we
    /// would silently start parallel imports.
    /// </summary>
    [Test]
    public async Task TryClaim_ParallelDispatchers_ExactlyOneClaimsTheSlot()
    {
        var repo = new InMemoryImportOperationRepository();
        const int parallelism = 16;
        var barrier = new TaskCompletionSource();

        var tasks = Enumerable.Range(0, parallelism).Select(async i =>
        {
            await barrier.Task;
            return await repo.TryClaimAsync(new("nam:sys-a"), ImportOperationKind.SimplyPlural, new($"idem-{i}"));
        }).ToArray();

        barrier.SetResult();
        var claims = await Task.WhenAll(tasks);

        var winners = claims.Count(c => c.IsNew);
        var distinctOperationIds = claims.Select(c => c.OperationId).Distinct().Count();

        using (Assert.Multiple())
        {
            await Assert.That(winners).IsEqualTo(1)
                .Because("Only one of the racing dispatchers may observe IsNew=true; more than one means we lost the mutex contract and would start parallel imports.");
            await Assert.That(distinctOperationIds).IsEqualTo(1)
                .Because("All 16 tasks must return the same operation_id (the winning one) so they all subscribe to the same WebSocket completion frame.");
        }
    }

    /// <summary>
    /// MarkRunning is a no-op when the row isn't in Queued. Pins the idempotency of the
    /// pickup transition so a re-delivered queue item can't accidentally overwrite a
    /// terminal status.
    /// </summary>
    [Test]
    public async Task MarkRunning_OnAlreadyTerminal_IsNoOp()
    {
        var repo = new InMemoryImportOperationRepository();
        var claim = await repo.TryClaimAsync(new("nam:sys-a"), ImportOperationKind.SimplyPlural, new("idem-1"));
        await repo.MarkSucceededAsync(new("nam:sys-a"), claim.OperationId, ImportOperationKind.SimplyPlural, alterCount: 3);

        await repo.MarkRunningAsync(new("nam:sys-a"), claim.OperationId);

        var snapshot = await repo.GetByIdAsync(new("nam:sys-a"), claim.OperationId);
        await Assert.That(snapshot!.Status).IsEqualTo(ImportOperationStatus.Succeeded)
            .Because("MarkRunning must not regress a terminal Succeeded status — the only valid transition is Queued -> Running.");
    }

    /// <summary>
    /// Stale-running sweep only returns rows older than the threshold. Pins the
    /// startup-sweep query so a fresh in-flight job is never rewritten to failed by
    /// accident.
    /// </summary>
    [Test]
    public async Task GetStaleRunning_OnlyReturnsRowsOlderThanThreshold()
    {
        var repo = new InMemoryImportOperationRepository();
        var claim = await repo.TryClaimAsync(new("nam:sys-a"), ImportOperationKind.SimplyPlural, new("idem-1"));
        await repo.MarkRunningAsync(new("nam:sys-a"), claim.OperationId);

        // A fresh row (started_at = now) shouldn't be returned for a 10 minute threshold.
        var stale = await repo.GetStaleRunningAsync(TimeSpan.FromMinutes(10));

        await Assert.That(stale.Count).IsEqualTo(0)
            .Because("A row that just started must never be classified as stale; otherwise the worker's startup sweep would fail in-flight imports on every restart.");
    }
}
