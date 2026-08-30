using Interfold.IntegrationTests.Shared.TestServices.TestBench;

namespace Interfold.IntegrationTests.Shared.UnitTests.TestBench;

/// <summary>Filesystem-level unit coverage for <see cref="TestBenchLease"/>. Every test
/// swaps in a per-test scratch directory via <see cref="TestBenchLease.TestOnlyBaseDirectoryOverride"/>
/// and marks itself <c>[NotInParallel]</c> because that override is a static.</summary>
[NotInParallel(nameof(TestBenchLeaseTests))]
public sealed class TestBenchLeaseTests
{
    // Scratch base directory reset in Setup so parallel test classes cannot see each other's
    // half-written state files. Kept per-instance so an assertion mid-test can still
    // diagnose from the folder on failure without racing peer tests.
    private string _scratchBase = null!;

    [Before(Test)]
    public void ArrangeScratchLeaseDir()
    {
        _scratchBase = Path.Combine(Path.GetTempPath(),
            "test-bench-lease-tests-" + Guid.NewGuid().ToString("N"));
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
    public async Task AcquireAsync_HeldExclusively_UntilDisposed()
    {
        // A holds the lock. A second AcquireAsync with a short timeout must fail with
        // TimeoutException because the file cannot be opened FileShare.None twice.
        using (await TestBenchLease.AcquireAsync(TimeSpan.FromSeconds(2), CancellationToken.None))
        {
            var thrown = await Assert.ThrowsAsync<TimeoutException>(async () =>
                await TestBenchLease.AcquireAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None));
            await Assert.That(thrown!.Message).Contains("test-bench lock");
        }

        // After dispose the lock file must be gone (FileOptions.DeleteOnClose) and a new
        // Acquire must succeed within a normal timeout budget.
        await Assert.That(File.Exists(TestBenchLease.GetLockFilePath())).IsFalse();
        using var _ = await TestBenchLease.AcquireAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
    }

    [Test]
    public async Task AcquireAsync_ReleasesAcrossSequentialCallers()
    {
        // Serialise three acquire/dispose cycles — each must succeed inside the tight
        // budget because the previous handle released the lock.
        for (var i = 0; i < 3; i++)
        {
            using var _ = await TestBenchLease.AcquireAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        }
    }

    [Test]
    public async Task ReadState_MissingFile_ReturnsEmptyLease()
    {
        // No state file on disk → default lease. This is the fresh-boot case.
        var state = TestBenchLease.ReadState();
        using (Assert.Multiple())
        {
            await Assert.That(state).IsNotNull();
            await Assert.That(state.Users.Count).IsEqualTo(0);
            await Assert.That(state.MigrationsApplied.Count).IsEqualTo(0);
            await Assert.That(state.LastAttachUtc).IsEqualTo(DateTime.MinValue);
        }
    }

    [Test]
    public async Task WriteState_ThenReadState_RoundTrips()
    {
        var user = TestBenchLease.CurrentUser("unit-test");
        var launcher = TestBenchLease.CurrentUser("test-bench-launcher");
        var written = new TestBenchLeaseState
        {
            LastAttachUtc = new DateTime(2026, 07, 27, 14, 15, 16, DateTimeKind.Utc),
            Users = { user },
            MigrationsApplied = { ["postgres"] = true },
            MigrationsInProgress = { ["cql-seed-and-migrate:scylla"] = user },
            Launching = launcher,
        };
        TestBenchLease.WriteState(written);

        var read = TestBenchLease.ReadState();
        using (Assert.Multiple())
        {
            await Assert.That(read.LastAttachUtc).IsEqualTo(written.LastAttachUtc);
            await Assert.That(read.Users.Count).IsEqualTo(1);
            await Assert.That(read.Users[0].Pid).IsEqualTo(user.Pid);
            await Assert.That(read.Users[0].TestHost).IsEqualTo("unit-test");
            await Assert.That(read.MigrationsApplied["postgres"]).IsTrue();
            await Assert.That(read.MigrationsInProgress["cql-seed-and-migrate:scylla"].Pid).IsEqualTo(user.Pid);
            await Assert.That(read.Launching).IsNotNull();
            await Assert.That(read.Launching!.Pid).IsEqualTo(launcher.Pid);
            await Assert.That(read.Launching.TestHost).IsEqualTo("test-bench-launcher");
        }
    }

    [Test]
    public async Task ReadState_MalformedJson_TreatsAsFreshLease()
    {
        // Torn-write survivors and manual edits shouldn't wedge the coordinator; a garbled
        // state file must degrade to a fresh lease so the reaper / next attach can recover.
        File.WriteAllText(TestBenchLease.GetStateFilePath(), "{ this-is-not-json ]");
        var state = TestBenchLease.ReadState();
        await Assert.That(state.Users.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PruneStale_KeepsCurrentProcess_DropsDefunctPid()
    {
        var alive = TestBenchLease.CurrentUser();
        // A PID we're highly confident does not exist on any target host. We probe just in
        // case the OS wrapped: if the recorded creation time doesn't match today's real
        // process, IsAlive returns false regardless of PID identity.
        var dead = new TestBenchUser(Pid: 999_999_990,
            PidCreatedUtc: new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            TestHost: "dead");

        var pruned = TestBenchLease.PruneStale(new[] { alive, dead });

        using (Assert.Multiple())
        {
            await Assert.That(pruned.Count).IsEqualTo(1);
            await Assert.That(pruned[0].Pid).IsEqualTo(alive.Pid);
        }
    }

    [Test]
    public async Task IsAlive_CurrentProcess_ReturnsTrue()
    {
        var current = TestBenchLease.CurrentUser();
        await Assert.That(TestBenchLease.IsAlive(current)).IsTrue();
    }

    [Test]
    public async Task IsAlive_ImpossiblePid_ReturnsFalse()
    {
        var fake = new TestBenchUser(Pid: 999_999_990,
            PidCreatedUtc: DateTime.UtcNow,
            TestHost: "fake");
        await Assert.That(TestBenchLease.IsAlive(fake)).IsFalse();
    }

    [Test]
    public async Task IsAlive_CorrectPidWrongCreationTime_ReturnsFalse()
    {
        // Simulates PID wraparound: right number, wrong provenance. The creation-time
        // cross-check must reject this so a reused PID cannot masquerade as an old attach.
        var currentPid = System.Diagnostics.Process.GetCurrentProcess().Id;
        var mimic = new TestBenchUser(Pid: currentPid,
            PidCreatedUtc: new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            TestHost: "mimic");
        await Assert.That(TestBenchLease.IsAlive(mimic)).IsFalse();
    }

    [Test]
    public void DeleteState_TolerantOfMissingFile()
    {
        // Second delete must not throw — reaper calls this unconditionally after teardown.
        TestBenchLease.DeleteState();
        TestBenchLease.DeleteState();
    }
}
