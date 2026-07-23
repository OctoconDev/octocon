using System.Globalization;
using TUnit.Core.Interfaces;

namespace Interfold.Bootstrapper.IntegrationTests.Fixtures;

/// <summary>
/// Caps how many bootstrapper integration tests run concurrently across the whole assembly,
/// sized against the host / docker daemon's actual CPU, memory, and disk budget.
/// </summary>
/// <remarks>
/// <para>
/// Each active test does several <c>docker exec</c> calls into a per-fixture DinD container's
/// inner daemon, publishes an Aspire-managed compose stack (Scylla + Postgres + API), and
/// pins a per-fixture image layer plus inner named volumes to disk. Running too many in
/// parallel produces <c>BadGateway</c> exec responses, <c>docker probe timed out</c> health
/// check failures, and — historically — <c>No space left on device</c> runner crashes.
/// </para>
/// <para>
/// Resolution order (evaluated exactly once when TUnit first reads
/// <see cref="IParallelLimit.Limit"/>; the resulting value is captured in a keyed
/// <c>SemaphoreSlim(limit, limit)</c> for the whole session):
/// </para>
/// <list type="number">
///   <item>
///     If <c>INTERFOLD_DIND_PARALLEL_LIMIT</c> is set to a positive integer, use it verbatim.
///     Skips the auto-probe entirely; matches CI's explicit override on <c>ci-cd.yml</c>.
///   </item>
///   <item>
///     Otherwise, probe the docker daemon via <see cref="DinDResourceProbe.Probe"/>
///     (<c>docker info</c> for NCPU / MemTotal / DockerRootDir, with a host fallback via
///     <see cref="Environment.ProcessorCount"/> / <see cref="GC.GetGCMemoryInfo()"/> /
///     <see cref="DriveInfo"/>). On Windows Docker Desktop the daemon path is critical —
///     the WSL2 VM's budget is much smaller than the physical host, and probing the host
///     directly would over-provision.
///   </item>
///   <item>
///     Feed the snapshot into <see cref="DinDParallelLimitCalculator.Compute"/>, which
///     leaves the operator-requested headroom on each axis: <b>10% RAM free</b> after peak
///     load, <b>15% disk free</b> after peak load, and <b>~1.5 CPU per slot</b> (Scylla is
///     shard-per-core, dockerd + API round up).
///   </item>
///   <item>
///     Clamp to <c>[1, <see cref="DinDParallelLimitCalculator.HardCeiling"/>]</c>.
///   </item>
/// </list>
/// <para>
/// Diagnostic lines are emitted to stdout during construction so operators can see why
/// the runner picked a particular concurrency level. Both are prefixed with
/// <c>[DinDParallelLimit]</c> and land in the TUnit run log and any CI job log —
/// <c>[DinDParallelLimit] source=...</c> from the probe and <c>[DinDParallelLimit] limit=N ...</c>
/// from the choice.
/// </para>
/// <para>
/// Tuning env vars (all optional, invariant culture):
/// <list type="bullet">
///   <item><c>INTERFOLD_DIND_PARALLEL_LIMIT</c> — hard override; skips auto-probe.</item>
///   <item><c>INTERFOLD_DIND_CPU_PER_SLOT</c> — default 1.5 (double).</item>
///   <item><c>INTERFOLD_DIND_MEM_PER_SLOT_MB</c> — default 2560.</item>
///   <item><c>INTERFOLD_DIND_DISK_PER_SLOT_MB</c> — default 4096.</item>
///   <item><c>INTERFOLD_DIND_MEM_HEADROOM_PCT</c> — default 10.</item>
///   <item><c>INTERFOLD_DIND_DISK_HEADROOM_PCT</c> — default 15.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class DinDParallelLimit : IParallelLimit
{
    internal const string EnvVarName = "INTERFOLD_DIND_PARALLEL_LIMIT";

    public int Limit { get; } = ResolveLimit();

    private static int ResolveLimit()
    {
        var raw = Environment.GetEnvironmentVariable(EnvVarName);
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            Console.WriteLine($"[DinDParallelLimit] limit={parsed} ({EnvVarName} override)");
            return parsed;
        }

        var budget = DinDResourceProbe.Probe();
        var costs = SlotCosts.FromEnvironment(Environment.GetEnvironmentVariable);
        var cpuSlots = DinDParallelLimitCalculator.ComputeCpuSlots(budget.Cpus, costs.CpuPerSlot);
        var memSlots = DinDParallelLimitCalculator.ComputeMemorySlots(budget.TotalMemoryBytes, costs);
        var diskSlots = DinDParallelLimitCalculator.ComputeDiskSlots(budget.FreeDiskBytes, budget.TotalDiskBytes, costs);
        var limit = DinDParallelLimitCalculator.Compute(budget, costs);
        Console.WriteLine(
            $"[DinDParallelLimit] limit={limit} (cpuSlots={cpuSlots} memSlots={memSlots} diskSlots={diskSlots})");
        return limit;
    }
}
