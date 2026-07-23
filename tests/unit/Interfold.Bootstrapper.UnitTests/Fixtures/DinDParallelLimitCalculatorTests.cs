using Interfold.Bootstrapper.IntegrationTests.Fixtures;
using TUnit.Core;

namespace Interfold.Bootstrapper.UnitTests.Fixtures;

/// <summary>
/// Pure-math tests for <see cref="DinDParallelLimitCalculator"/>. Every case constructs
/// a <see cref="ResourceBudget"/> + <see cref="SlotCosts"/> pair explicitly so there's
/// zero coupling to the host, docker, or the file system.
/// </summary>
public sealed class DinDParallelLimitCalculatorTests
{
    private const long OneMebibyte = 1024L * 1024L;
    private const long OneGibibyte = 1024L * OneMebibyte;

    /// <summary>
    /// GH-hosted `ubuntu-latest` profile: 4 vCPU / 16 GiB RAM / ~14 GiB free of a
    /// ~30 GiB rootfs. Matches the value hardcoded on <c>ci-cd.yml:28</c> today
    /// (<c>INTERFOLD_DIND_PARALLEL_LIMIT: '2'</c>) — the whole point of this change
    /// is that CI's auto-computed number lands on the same 2 without the env var.
    /// </summary>
    [Test]
    public async Task GitHubUbuntuLatestProfileClampsToTwo()
    {
        var budget = new ResourceBudget(
            Cpus: 4,
            TotalMemoryBytes: 16 * OneGibibyte,
            FreeDiskBytes: 14 * OneGibibyte,
            TotalDiskBytes: 30 * OneGibibyte);
        var limit = DinDParallelLimitCalculator.Compute(budget, SlotCosts.Default);
        // cpuSlots=2, memSlots=5 (14.4 GiB / 2.5 GiB), diskSlots=2 ((14 - 4.5) / 4)
        await Assert.That(limit).IsEqualTo(2);
    }

    /// <summary>
    /// Windows Docker Desktop default WSL2 profile: 4 vCPU / 8 GiB / 40 GiB free of
    /// 50 GiB VHDX. Reproduces the local footgun the user just hit — running with
    /// the pre-change default of 6 collapsed the docker daemon.
    /// </summary>
    [Test]
    public async Task LaptopDockerDesktopProfileClampsToTwo()
    {
        var budget = new ResourceBudget(
            Cpus: 4,
            TotalMemoryBytes: 8 * OneGibibyte,
            FreeDiskBytes: 40 * OneGibibyte,
            TotalDiskBytes: 50 * OneGibibyte);
        var limit = DinDParallelLimitCalculator.Compute(budget, SlotCosts.Default);
        // cpuSlots=2, memSlots=2 (7.2 GiB / 2.5 GiB), diskSlots=8 ((40 - 7.5) / 4)
        await Assert.That(limit).IsEqualTo(2);
    }

    /// <summary>
    /// Fat workstation: 16 vCPU / 64 GiB / 500 GiB free of 1 TB. CPU dominates and
    /// produces 10 slots; the hard ceiling of 16 doesn't yet bite here.
    /// </summary>
    [Test]
    public async Task FatWorkstationClampsToCpuBudget()
    {
        var budget = new ResourceBudget(
            Cpus: 16,
            TotalMemoryBytes: 64 * OneGibibyte,
            FreeDiskBytes: 500 * OneGibibyte,
            TotalDiskBytes: 1000 * OneGibibyte);
        var limit = DinDParallelLimitCalculator.Compute(budget, SlotCosts.Default);
        // cpuSlots=10 (16/1.5), memSlots=23, diskSlots=87
        await Assert.That(limit).IsEqualTo(10);
    }

    /// <summary>
    /// The hard ceiling applies even when every axis wants to go higher. Prevents
    /// pathological over-scheduling of dockerd's exec throughput.
    /// </summary>
    [Test]
    public async Task ExtremelyLargeHostClampsToHardCeiling()
    {
        var budget = new ResourceBudget(
            Cpus: 128,
            TotalMemoryBytes: 512 * OneGibibyte,
            FreeDiskBytes: 4000 * OneGibibyte,
            TotalDiskBytes: 8000 * OneGibibyte);
        var limit = DinDParallelLimitCalculator.Compute(budget, SlotCosts.Default);
        await Assert.That(limit).IsEqualTo(DinDParallelLimitCalculator.HardCeiling);
    }

    /// <summary>
    /// Tiny box: 1 vCPU / 2 GiB / disk fine. Every axis wants to floor to 0; the
    /// outer <c>Clamp</c> in <see cref="DinDParallelLimitCalculator.Compute"/> lifts
    /// it to 1 so tests still make forward progress (they'll just serialize).
    /// </summary>
    [Test]
    public async Task TinyBoxFloorsToOne()
    {
        var budget = new ResourceBudget(
            Cpus: 1,
            TotalMemoryBytes: 2 * OneGibibyte,
            FreeDiskBytes: 20 * OneGibibyte,
            TotalDiskBytes: 30 * OneGibibyte);
        var limit = DinDParallelLimitCalculator.Compute(budget, SlotCosts.Default);
        // cpuSlots=0 (1/1.5), memSlots=0 (1.8 GiB / 2.5 GiB), diskSlots=3.875 -> 3
        await Assert.That(limit).IsEqualTo(1);
    }

    /// <summary>
    /// The disk axis reserves a percentage-of-total floor. A drive already tighter
    /// than the operator-requested headroom gives 0 for that axis (post-clamp: 1)
    /// so we don't try to run anything that will blow past the safety line.
    /// </summary>
    [Test]
    public async Task DiskAlreadyBelowHeadroomForcesFloor()
    {
        // 100 GiB drive, only 10 GiB free. Headroom line = 15 GiB, so the calc
        // computes usable = 10 - 15 = -5 GiB → 0 disk slots. Clamp lifts to 1.
        var budget = new ResourceBudget(
            Cpus: 8,
            TotalMemoryBytes: 32 * OneGibibyte,
            FreeDiskBytes: 10 * OneGibibyte,
            TotalDiskBytes: 100 * OneGibibyte);
        var limit = DinDParallelLimitCalculator.Compute(budget, SlotCosts.Default);
        await Assert.That(limit).IsEqualTo(1);
    }

    /// <summary>
    /// Memory-per-slot env override lowers the memory-axis cost so more slots fit.
    /// Round-trips through <see cref="SlotCosts.FromEnvironment"/> from a fake env.
    /// </summary>
    [Test]
    public async Task MemoryPerSlotOverrideRaisesMemorySlots()
    {
        var env = new Dictionary<string, string?>
        {
            ["INTERFOLD_DIND_MEM_PER_SLOT_MB"] = "1024", // 1 GiB per slot (half the default)
        };
        var costs = SlotCosts.FromEnvironment(k => env.TryGetValue(k, out var v) ? v : null);
        var budget = new ResourceBudget(
            Cpus: 16, // takes CPU out of the picture
            TotalMemoryBytes: 8 * OneGibibyte,
            FreeDiskBytes: 500 * OneGibibyte,
            TotalDiskBytes: 1000 * OneGibibyte);
        var limit = DinDParallelLimitCalculator.Compute(budget, costs);
        // memSlots = 7.2 GiB / 1 GiB = 7 (headroom 10%)
        await Assert.That(limit).IsEqualTo(7);
    }

    /// <summary>
    /// CPU-per-slot override tightens or loosens the CPU axis. Uses invariant
    /// culture (a hostile "3,0" would parse to 30 on de-DE otherwise).
    /// </summary>
    [Test]
    public async Task CpuPerSlotOverrideChangesCpuSlots()
    {
        var env = new Dictionary<string, string?>
        {
            ["INTERFOLD_DIND_CPU_PER_SLOT"] = "0.5", // 0.5 cores per slot
        };
        var costs = SlotCosts.FromEnvironment(k => env.TryGetValue(k, out var v) ? v : null);
        var budget = new ResourceBudget(
            Cpus: 4,
            TotalMemoryBytes: 64 * OneGibibyte, // huge, takes memory out
            FreeDiskBytes: 500 * OneGibibyte,
            TotalDiskBytes: 1000 * OneGibibyte);
        var limit = DinDParallelLimitCalculator.Compute(budget, costs);
        // cpuSlots = 4 / 0.5 = 8, everything else huge
        await Assert.That(limit).IsEqualTo(8);
    }

    /// <summary>
    /// Headroom percentages are honoured. Bumping mem headroom to 50% halves the
    /// usable RAM and roughly halves the memory-axis slots — proves the field is
    /// wired end-to-end from env → cost record → math.
    /// </summary>
    [Test]
    public async Task HeadroomPercentageOverrideIsHonoured()
    {
        var env = new Dictionary<string, string?>
        {
            ["INTERFOLD_DIND_MEM_HEADROOM_PCT"] = "50",
        };
        var costs = SlotCosts.FromEnvironment(k => env.TryGetValue(k, out var v) ? v : null);
        var budget = new ResourceBudget(
            Cpus: 32,
            TotalMemoryBytes: 20 * OneGibibyte,
            FreeDiskBytes: 500 * OneGibibyte,
            TotalDiskBytes: 1000 * OneGibibyte);
        var memSlots = DinDParallelLimitCalculator.ComputeMemorySlots(budget.TotalMemoryBytes, costs);
        // usable = 20 GiB * 0.5 = 10 GiB. memSlots = 10 / 2.5 = 4.
        await Assert.That(memSlots).IsEqualTo(4);
        var limit = DinDParallelLimitCalculator.Compute(budget, costs);
        await Assert.That(limit).IsEqualTo(4);
    }

    /// <summary>
    /// Non-parseable / zero / negative env values fall back to the baked-in default
    /// rather than blowing up the process at construction. Guards against a hostile
    /// <c>INTERFOLD_DIND_CPU_PER_SLOT=abc</c> or <c>INTERFOLD_DIND_MEM_PER_SLOT_MB=0</c>.
    /// </summary>
    [Test]
    public async Task GarbageEnvValuesFallBackToDefaults()
    {
        var env = new Dictionary<string, string?>
        {
            ["INTERFOLD_DIND_CPU_PER_SLOT"] = "not-a-double",
            ["INTERFOLD_DIND_MEM_PER_SLOT_MB"] = "0",
            ["INTERFOLD_DIND_DISK_PER_SLOT_MB"] = "-42",
            ["INTERFOLD_DIND_MEM_HEADROOM_PCT"] = "abc",
            ["INTERFOLD_DIND_DISK_HEADROOM_PCT"] = "",
        };
        var costs = SlotCosts.FromEnvironment(k => env.TryGetValue(k, out var v) ? v : null);
        await Assert.That(costs.CpuPerSlot).IsEqualTo(SlotCosts.DefaultCpuPerSlot);
        await Assert.That(costs.MemPerSlotBytes).IsEqualTo(SlotCosts.DefaultMemPerSlotBytes);
        await Assert.That(costs.DiskPerSlotBytes).IsEqualTo(SlotCosts.DefaultDiskPerSlotBytes);
        await Assert.That(costs.MemHeadroomPct).IsEqualTo(SlotCosts.DefaultMemHeadroomPct);
        await Assert.That(costs.DiskHeadroomPct).IsEqualTo(SlotCosts.DefaultDiskHeadroomPct);
    }

    /// <summary>
    /// Zero-cost sentinel means "this axis is disabled": returns
    /// <see cref="DinDParallelLimitCalculator.HardCeiling"/> so the Min ignores it.
    /// Useful for the future when someone wants CPU-only sizing on a diskless VM.
    /// </summary>
    [Test]
    public async Task ZeroCostAxisDropsOutOfMin()
    {
        var budget = new ResourceBudget(
            Cpus: 4,
            TotalMemoryBytes: 16 * OneGibibyte,
            FreeDiskBytes: 100 * OneGibibyte,
            TotalDiskBytes: 200 * OneGibibyte);
        var costs = new SlotCosts(
            CpuPerSlot: SlotCosts.DefaultCpuPerSlot,
            MemPerSlotBytes: 0,
            DiskPerSlotBytes: 0,
            MemHeadroomPct: 10,
            DiskHeadroomPct: 15);
        var memSlots = DinDParallelLimitCalculator.ComputeMemorySlots(budget.TotalMemoryBytes, costs);
        var diskSlots = DinDParallelLimitCalculator.ComputeDiskSlots(budget.FreeDiskBytes, budget.TotalDiskBytes, costs);
        await Assert.That(memSlots).IsEqualTo(DinDParallelLimitCalculator.HardCeiling);
        await Assert.That(diskSlots).IsEqualTo(DinDParallelLimitCalculator.HardCeiling);
        var limit = DinDParallelLimitCalculator.Compute(budget, costs);
        await Assert.That(limit).IsEqualTo(2); // still gated by CPU axis (4 / 1.5 = 2)
    }

    /// <summary>
    /// Zero / unknown CPU count still yields a working suite: floor of 1 keeps tests
    /// serial. Simulates a broken <c>docker info</c> parse that gave us NCPU=0.
    /// </summary>
    [Test]
    public async Task ZeroCpuBudgetStillFloorsToOne()
    {
        var budget = new ResourceBudget(
            Cpus: 0,
            TotalMemoryBytes: 16 * OneGibibyte,
            FreeDiskBytes: 200 * OneGibibyte,
            TotalDiskBytes: 400 * OneGibibyte);
        var limit = DinDParallelLimitCalculator.Compute(budget, SlotCosts.Default);
        await Assert.That(limit).IsEqualTo(1);
    }
}
