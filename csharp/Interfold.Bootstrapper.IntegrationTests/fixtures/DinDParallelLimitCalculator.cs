using System.Globalization;

namespace Interfold.Bootstrapper.IntegrationTests.Fixtures;

/// <summary>
/// Pure math for turning a host / docker-daemon resource snapshot into a safe upper
/// bound on concurrent DinD test scenarios. All methods are side-effect free so the
/// numbers can be asserted directly in <c>Interfold.Bootstrapper.UnitTests</c> without
/// touching Docker or the file system.
/// </summary>
/// <remarks>
/// The three axes (CPU, memory, disk) are combined by <see cref="Compute"/> as
/// <c>Min(cpuSlots, memSlots, diskSlots)</c> and then clamped into
/// <c>[1, <see cref="HardCeiling"/>]</c>. The floor of 1 guarantees forward progress
/// even on a tightly constrained box; the ceiling of 16 prevents pathological
/// over-scheduling on very fat hosts (nobody usefully runs more than 16 concurrent
/// DinDs — dockerd's exec throughput plateaus long before that).
/// </remarks>
public static class DinDParallelLimitCalculator
{
    /// <summary>
    /// Upper bound the calculator will ever return. Applied after the three-axis Min so
    /// even a machine with ample RAM / disk / CPU never spins up more DinDs than
    /// dockerd can usefully juggle.
    /// </summary>
    public const int HardCeiling = 16;

    /// <summary>
    /// Combines the three axes and clamps into <c>[1, <see cref="HardCeiling"/>]</c>.
    /// </summary>
    public static int Compute(ResourceBudget budget, SlotCosts costs)
    {
        var cpuSlots = ComputeCpuSlots(budget.Cpus, costs.CpuPerSlot);
        var memSlots = ComputeMemorySlots(budget.TotalMemoryBytes, costs);
        var diskSlots = ComputeDiskSlots(budget.FreeDiskBytes, budget.TotalDiskBytes, costs);
        var min = Math.Min(cpuSlots, Math.Min(memSlots, diskSlots));
        return Math.Clamp(min, 1, HardCeiling);
    }

    /// <summary>
    /// Slots supported by the CPU budget: <c>floor(cpus / cpuPerSlot)</c>. Returns
    /// <see cref="HardCeiling"/> when <paramref name="cpuPerSlot"/> is non-positive so
    /// this axis effectively drops out of the Min.
    /// </summary>
    public static int ComputeCpuSlots(int cpus, double cpuPerSlot)
    {
        if (cpuPerSlot <= 0) return HardCeiling;
        if (cpus <= 0) return 0;
        return (int)Math.Floor(cpus / cpuPerSlot);
    }

    /// <summary>
    /// Slots supported by the memory budget after reserving the headroom fraction.
    /// <c>floor((totalMem * (1 - headroom%)) / memPerSlot)</c>. Returns
    /// <see cref="HardCeiling"/> when <see cref="SlotCosts.MemPerSlotBytes"/> is
    /// non-positive.
    /// </summary>
    public static int ComputeMemorySlots(long totalMemoryBytes, SlotCosts costs)
    {
        if (costs.MemPerSlotBytes <= 0) return HardCeiling;
        if (totalMemoryBytes <= 0) return 0;
        var headroomPct = Math.Clamp(costs.MemHeadroomPct, 0, 100);
        var usable = (long)(totalMemoryBytes * ((100 - headroomPct) / 100.0));
        if (usable <= 0) return 0;
        var slots = usable / costs.MemPerSlotBytes;
        return slots > int.MaxValue ? int.MaxValue : (int)slots;
    }

    /// <summary>
    /// Slots supported by the disk budget after reserving the headroom fraction of the
    /// total drive. <c>floor((freeDisk - totalDisk * headroom%) / diskPerSlot)</c>.
    /// Returns 0 if the drive is already tighter than the headroom (operators see the
    /// diagnostic line and can free space or override); the outer clamp lifts to 1 so
    /// the suite still tries to run one at a time. Returns <see cref="HardCeiling"/>
    /// when <see cref="SlotCosts.DiskPerSlotBytes"/> is non-positive.
    /// </summary>
    public static int ComputeDiskSlots(long freeDiskBytes, long totalDiskBytes, SlotCosts costs)
    {
        if (costs.DiskPerSlotBytes <= 0) return HardCeiling;
        if (freeDiskBytes <= 0 || totalDiskBytes <= 0) return 0;
        var headroomPct = Math.Clamp(costs.DiskHeadroomPct, 0, 100);
        var floorBytes = (long)(totalDiskBytes * (headroomPct / 100.0));
        var usable = freeDiskBytes - floorBytes;
        if (usable <= 0) return 0;
        var slots = usable / costs.DiskPerSlotBytes;
        return slots > int.MaxValue ? int.MaxValue : (int)slots;
    }
}

/// <summary>
/// Snapshot of what the host / docker daemon reports at probe time.
/// Deliberately dumb POCO so unit tests can construct arbitrary combinations.
/// </summary>
/// <param name="Cpus">Logical CPU count (0 or negative signals "unknown").</param>
/// <param name="TotalMemoryBytes">Total physical memory available to the daemon or process.</param>
/// <param name="FreeDiskBytes">Free bytes on the drive holding docker's root (or the OS drive on fallback).</param>
/// <param name="TotalDiskBytes">Total bytes on the same drive; used to compute the disk headroom floor.</param>
public readonly record struct ResourceBudget(
    int Cpus,
    long TotalMemoryBytes,
    long FreeDiskBytes,
    long TotalDiskBytes);

/// <summary>
/// Per-slot resource cost + free-space headroom expectations. Defaults are tuned
/// against the fixture inventory: outer DinD idle (~0.5 GB) plus a peak inner
/// compose stack (Scylla ~1.5 GB + Postgres ~0.2 GB + API ~0.2 GB) plus safety.
/// Each field is env-overridable via <see cref="FromEnvironment"/>.
/// </summary>
public readonly record struct SlotCosts(
    double CpuPerSlot,
    long MemPerSlotBytes,
    long DiskPerSlotBytes,
    int MemHeadroomPct,
    int DiskHeadroomPct)
{
    /// <summary>Default CPU units reserved per active DinD slot (1 shard/core for Scylla + dockerd overhead).</summary>
    public const double DefaultCpuPerSlot = 1.5;
    /// <summary>Default memory reserved per active DinD slot (outer + inner peak).</summary>
    public const long DefaultMemPerSlotBytes = 2560L * 1024 * 1024;
    /// <summary>Default disk reserved per active DinD slot (per-fixture image layers + inner volumes + logs).</summary>
    public const long DefaultDiskPerSlotBytes = 4096L * 1024 * 1024;
    /// <summary>Default free-memory percentage the calculator preserves after peak load.</summary>
    public const int DefaultMemHeadroomPct = 10;
    /// <summary>Default free-disk percentage the calculator preserves after peak load.</summary>
    public const int DefaultDiskHeadroomPct = 15;

    /// <summary>Baked-in defaults, suitable for the fixture inventory as of writing.</summary>
    public static SlotCosts Default => new(
        DefaultCpuPerSlot,
        DefaultMemPerSlotBytes,
        DefaultDiskPerSlotBytes,
        DefaultMemHeadroomPct,
        DefaultDiskHeadroomPct);

    /// <summary>
    /// Reads each per-slot cost + headroom from the environment, falling back to the
    /// baked-in default on missing / non-parseable / non-positive values. The
    /// <paramref name="getEnv"/> indirection keeps this testable without touching
    /// process environment.
    /// </summary>
    public static SlotCosts FromEnvironment(Func<string, string?> getEnv) => new(
        ReadDouble(getEnv, "INTERFOLD_DIND_CPU_PER_SLOT", DefaultCpuPerSlot),
        ReadMebibytes(getEnv, "INTERFOLD_DIND_MEM_PER_SLOT_MB", DefaultMemPerSlotBytes),
        ReadMebibytes(getEnv, "INTERFOLD_DIND_DISK_PER_SLOT_MB", DefaultDiskPerSlotBytes),
        ReadInt(getEnv, "INTERFOLD_DIND_MEM_HEADROOM_PCT", DefaultMemHeadroomPct),
        ReadInt(getEnv, "INTERFOLD_DIND_DISK_HEADROOM_PCT", DefaultDiskHeadroomPct));

    private static double ReadDouble(Func<string, string?> getEnv, string key, double fallback)
    {
        var raw = getEnv(key);
        return !string.IsNullOrWhiteSpace(raw)
               && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
               && value > 0
            ? value
            : fallback;
    }

    private static int ReadInt(Func<string, string?> getEnv, string key, int fallback)
    {
        var raw = getEnv(key);
        return !string.IsNullOrWhiteSpace(raw)
               && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
               && value >= 0
            ? value
            : fallback;
    }

    private static long ReadMebibytes(Func<string, string?> getEnv, string key, long fallbackBytes)
    {
        var raw = getEnv(key);
        return !string.IsNullOrWhiteSpace(raw)
               && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb)
               && mb > 0
            ? mb * 1024L * 1024L
            : fallbackBytes;
    }
}
