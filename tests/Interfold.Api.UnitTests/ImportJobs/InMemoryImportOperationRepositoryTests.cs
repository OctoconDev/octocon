using Interfold.Contracts.Models.ImportOperations;
using Interfold.Infrastructure.InMemory.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Api.UnitTests.ImportJobs;

// Per-system mutex contract for the in-memory IImportOperationRepository. The InMemory
// port must mirror the Scylla port so integration tests against InMemory are a
// meaningful proxy for the production Cassandra LWT semantics: at most one
// queued-or-running operation per (system, kind) pair.
public sealed class InMemoryImportOperationRepositoryTests
{
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

    // Load-bearing invariant: sequential claims collapse. Regression = duplicate-import bug.
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

    // Strongest mutex test: 16 concurrent tasks must converge on one winner.
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

    // Idempotency of pickup: a re-delivered queue item must not overwrite a terminal.
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

    // Startup-sweep must never rewrite a fresh in-flight job to failed.
    [Test]
    public async Task GetStaleRunning_OnlyReturnsRowsOlderThanThreshold()
    {
        var repo = new InMemoryImportOperationRepository();
        var claim = await repo.TryClaimAsync(new("nam:sys-a"), ImportOperationKind.SimplyPlural, new("idem-1"));
        await repo.MarkRunningAsync(new("nam:sys-a"), claim.OperationId);

        var stale = await repo.GetStaleRunningAsync(TimeSpan.FromMinutes(10));

        await Assert.That(stale.Count).IsEqualTo(0)
            .Because("A row that just started must never be classified as stale; otherwise the worker's startup sweep would fail in-flight imports on every restart.");
    }
}
