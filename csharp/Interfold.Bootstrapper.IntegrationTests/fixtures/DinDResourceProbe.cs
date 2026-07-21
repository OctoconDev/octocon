using System.Diagnostics;
using System.Text.Json;

namespace Interfold.Bootstrapper.IntegrationTests.Fixtures;

/// <summary>
/// Best-effort snapshot of the resource budget available to concurrent DinD tests.
/// Tries the docker daemon first (via <c>docker info</c>) because that's the ceiling
/// that actually matters when the test scenarios are all remote-controlled containers.
/// On Windows Docker Desktop the daemon's <c>NCPU</c> / <c>MemTotal</c> reflect the
/// WSL2 VM, which is usually much smaller than the physical host — asking the host
/// directly would over-provision. If <c>docker</c> isn't on PATH, times out, or returns
/// malformed JSON, falls back to the host: <see cref="Environment.ProcessorCount"/>,
/// <see cref="GC.GetGCMemoryInfo()"/>, and <see cref="DriveInfo"/> against the OS drive.
/// </summary>
/// <remarks>
/// Never throws — probe is best-effort by contract. Emits exactly one diagnostic line
/// per invocation to stdout so operators can see which source and which numbers drove
/// the sizing decision. The line is prefixed with <c>[DinDParallelLimit]</c> so it
/// grep-groups with the choice diagnostic emitted by <see cref="DinDParallelLimit"/>.
/// </remarks>
public static class DinDResourceProbe
{
    private static readonly TimeSpan DockerInfoTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Samples the daemon (preferred) or host (fallback) resources exactly once and
    /// returns the numbers as a <see cref="ResourceBudget"/>. Also writes a
    /// <c>[DinDParallelLimit] source=&lt;docker|host&gt; ...</c> line to stdout.
    /// </summary>
    public static ResourceBudget Probe()
    {
        var docker = TryProbeDocker();
        if (docker is { } b)
        {
            EmitDiagnostic("docker", b);
            return b;
        }
        var host = ProbeHost();
        EmitDiagnostic("host", host);
        return host;
    }

    private static ResourceBudget? TryProbeDocker()
    {
        try
        {
            var psi = new ProcessStartInfo("docker", "info --format {{json .}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(DockerInfoTimeout))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return null;
            }
            _ = stderr; // drained purely to keep the pipe from stalling

            if (proc.ExitCode != 0) return null;

            var json = stdout.GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(json)) return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var ncpu = TryInt(root, "NCPU");
            var memTotal = TryLong(root, "MemTotal");
            var dockerRoot = root.TryGetProperty("DockerRootDir", out var drEl) ? drEl.GetString() : null;

            if (ncpu <= 0 || memTotal <= 0) return null;

            var (freeDisk, totalDisk) = ResolveDiskFor(dockerRoot);
            if (freeDisk <= 0 || totalDisk <= 0) return null;

            return new ResourceBudget(ncpu, memTotal, freeDisk, totalDisk);
        }
        catch
        {
            return null;
        }
    }

    private static ResourceBudget ProbeHost()
    {
        var cpus = Environment.ProcessorCount;
        // TotalAvailableMemoryBytes reflects the container cgroup cap (if any) or the
        // physical RAM the GC can address, which is what we want as a ceiling — not
        // WorkingSet or CommittedBytes, which are process-scoped.
        var totalMem = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var (freeDisk, totalDisk) = ResolveDiskFor(preferredPath: null);
        return new ResourceBudget(cpus, totalMem, freeDisk, totalDisk);
    }

    /// <summary>
    /// Best-effort disk stats. Tries <paramref name="preferredPath"/> first (docker
    /// daemon's <c>DockerRootDir</c> when we could parse it), then the drive holding
    /// the test binaries. On Windows Docker Desktop the docker root is a path inside
    /// the WSL2 VM (<c>/var/lib/docker</c>) that doesn't exist on the host — that
    /// call fails and we drop through to <c>C:\</c>, whose free space is a fair proxy
    /// for the WSL2 VHDX-backed disk anyway (Docker Desktop stores it on the host).
    /// </summary>
    private static (long free, long total) ResolveDiskFor(string? preferredPath)
    {
        foreach (var path in new[] { preferredPath, Path.GetPathRoot(AppContext.BaseDirectory) })
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                var drive = new DriveInfo(path);
                if (drive.IsReady && drive.TotalSize > 0)
                {
                    return (drive.AvailableFreeSpace, drive.TotalSize);
                }
            }
            catch
            {
                // try the next candidate
            }
        }
        return (0, 0);
    }

    private static int TryInt(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v)
            ? v
            : 0;

    private static long TryLong(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v)
            ? v
            : 0L;

    private static void EmitDiagnostic(string source, ResourceBudget budget)
    {
        const double gb = 1024.0 * 1024.0 * 1024.0;
        var memGb = budget.TotalMemoryBytes / gb;
        var freeGb = budget.FreeDiskBytes / gb;
        var totalGb = budget.TotalDiskBytes / gb;
        Console.WriteLine(
            $"[DinDParallelLimit] source={source} cpu={budget.Cpus} mem={memGb:F1}GB disk={freeGb:F1}GB free of {totalGb:F1}GB");
    }
}
