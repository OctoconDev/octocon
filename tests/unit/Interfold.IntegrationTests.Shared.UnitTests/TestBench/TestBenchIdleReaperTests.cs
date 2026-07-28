using Interfold.IntegrationTests.Shared.TestServices.TestBench;

namespace Interfold.IntegrationTests.Shared.UnitTests.TestBench;

/// <summary>Decision-only coverage for <see cref="TestBenchIdleReaper.RunIfIdleAsync"/>.
/// The real docker-rm path is deliberately not exercised here (it would require
/// live containers); we cover every combination of <c>LastAttachUtc</c> and
/// <c>Users</c> that drives the boolean return.</summary>
[NotInParallel(nameof(TestBenchIdleReaperTests))]
public sealed class TestBenchIdleReaperTests
{
    private string _scratchBase = null!;

    [Before(Test)]
    public void ArrangeScratchLeaseDir()
    {
        _scratchBase = Path.Combine(Path.GetTempPath(),
            "test-bench-reaper-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchBase);
        TestBenchLease.TestOnlyBaseDirectoryOverride = _scratchBase;
    }

    [After(Test)]
    public void CleanupScratchLeaseDir()
    {
        TestBenchLease.TestOnlyBaseDirectoryOverride = null;
        try { Directory.Delete(_scratchBase, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Test]
    public async Task RunIfIdleAsync_FreshLease_DoesNothing()
    {
        // LastAttachUtc == DateTime.MinValue → nothing to reap yet.
        var state = new TestBenchLeaseState();
        var ran = await TestBenchIdleReaper.RunIfIdleAsync(state, CancellationToken.None);
        await Assert.That(ran).IsFalse();
    }

    [Test]
    public async Task RunIfIdleAsync_WithLiveUsers_DoesNothing()
    {
        // Live user attached, well past idle timeout — reaper must NOT tear down.
        var state = new TestBenchLeaseState
        {
            LastAttachUtc = DateTime.UtcNow - TimeSpan.FromDays(1),
            Users = { TestBenchLease.CurrentUser() },
        };
        var ran = await TestBenchIdleReaper.RunIfIdleAsync(state, CancellationToken.None);
        await Assert.That(ran).IsFalse();
    }

    [Test]
    public async Task RunIfIdleAsync_RecentAttachEmptyUsers_DoesNothing()
    {
        // Nobody attached but the bench was touched a moment ago — still within idle grace.
        var state = new TestBenchLeaseState
        {
            LastAttachUtc = DateTime.UtcNow - TimeSpan.FromSeconds(30),
            Users = { },
        };
        var ran = await TestBenchIdleReaper.RunIfIdleAsync(state, CancellationToken.None);
        await Assert.That(ran).IsFalse();
    }

    [Test]
    public async Task ResolveIdleTimeout_DefaultsToFifteenMinutes_WithoutEnv()
    {
        var previous = Environment.GetEnvironmentVariable("INTERFOLD_TEST_BENCH_IDLE_TIMEOUT_MINUTES");
        Environment.SetEnvironmentVariable("INTERFOLD_TEST_BENCH_IDLE_TIMEOUT_MINUTES", null);
        try
        {
            await Assert.That(TestBenchIdleReaper.ResolveIdleTimeout())
                .IsEqualTo(TestBenchIdleReaper.DefaultIdleTimeout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTERFOLD_TEST_BENCH_IDLE_TIMEOUT_MINUTES", previous);
        }
    }

    [Test]
    public async Task ResolveIdleTimeout_PositiveEnv_Overrides()
    {
        var previous = Environment.GetEnvironmentVariable("INTERFOLD_TEST_BENCH_IDLE_TIMEOUT_MINUTES");
        Environment.SetEnvironmentVariable("INTERFOLD_TEST_BENCH_IDLE_TIMEOUT_MINUTES", "3");
        try
        {
            await Assert.That(TestBenchIdleReaper.ResolveIdleTimeout())
                .IsEqualTo(TimeSpan.FromMinutes(3));
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTERFOLD_TEST_BENCH_IDLE_TIMEOUT_MINUTES", previous);
        }
    }

    [Test]
    public async Task ResolveIdleTimeout_MalformedEnv_FallsBackToDefault()
    {
        var previous = Environment.GetEnvironmentVariable("INTERFOLD_TEST_BENCH_IDLE_TIMEOUT_MINUTES");
        Environment.SetEnvironmentVariable("INTERFOLD_TEST_BENCH_IDLE_TIMEOUT_MINUTES", "not-a-number");
        try
        {
            await Assert.That(TestBenchIdleReaper.ResolveIdleTimeout())
                .IsEqualTo(TestBenchIdleReaper.DefaultIdleTimeout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTERFOLD_TEST_BENCH_IDLE_TIMEOUT_MINUTES", previous);
        }
    }
}
