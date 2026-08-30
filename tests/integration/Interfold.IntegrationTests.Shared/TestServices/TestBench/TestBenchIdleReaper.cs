using System.Diagnostics;
using System.Globalization;

namespace Interfold.IntegrationTests.Shared.TestServices.TestBench;

/// <summary>Opportunistically tears down the bench when it's been idle. Invoked at the top
/// of <see cref="TestBenchCoordinator.EnsureRunningAsync"/> under the same cross-process
/// lock, so exactly one reaper decision runs at a time. Runs <c>docker rm -f</c> against
/// the three pinned container names from <see cref="TestBenchContainerNames"/>. No named
/// volumes need cleaning up: bench containers hold their data in their own filesystem
/// (see <c>InterfoldAppHost.cs</c> where <c>testBenchMode</c> takes the
/// <c>WithLifetime(Persistent)</c> branch instead of <c>AsPersistent(volumeName, path)</c>),
/// so <c>docker rm -f</c> discards everything in one call and the next cold-boot always
/// gets a fresh init with the current <c>TestDbCredentials</c>.</summary>
public static class TestBenchIdleReaper
{
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(15);
    private const string IdleTimeoutEnv = "INTERFOLD_TEST_BENCH_IDLE_TIMEOUT_MINUTES";

    /// <summary>Returns the idle timeout — the env var when set to a positive integer,
    /// otherwise <see cref="DefaultIdleTimeout"/>.</summary>
    public static TimeSpan ResolveIdleTimeout()
    {
        var raw = Environment.GetEnvironmentVariable(IdleTimeoutEnv);
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            && minutes > 0)
        {
            return TimeSpan.FromMinutes(minutes);
        }
        return DefaultIdleTimeout;
    }

    /// <summary>Runs the reaper decision. If the lease has been idle longer than the
    /// timeout AND no live user PIDs remain, <c>docker rm -f</c> the bench containers and
    /// delete the state file. Returns <c>true</c> when a teardown actually ran.</summary>
    public static async Task<bool> RunIfIdleAsync(TestBenchLeaseState state, CancellationToken ct)
    {
        var idle = DateTime.UtcNow - state.LastAttachUtc;
        var timeout = ResolveIdleTimeout();

        // Empty users list is common on the very first attach — the LastAttachUtc default
        // (DateTime.MinValue) drives the age check to a huge positive value which would
        // spuriously trip the reaper. Skip when there is nothing yet to reap.
        if (state.LastAttachUtc == DateTime.MinValue) return false;
        if (state.Users.Count > 0) return false;
        if (idle < timeout) return false;

        await TeardownAsync(ct).ConfigureAwait(false);
        TestBenchLease.DeleteState();
        return true;
    }

    /// <summary>Force-remove the bench containers unconditionally. Used by tests and by
    /// external scripts that want to nuke the bench without waiting for the idle window.</summary>
    public static async Task TeardownAsync(CancellationToken ct)
    {
        foreach (var name in TestBenchContainerNames.All)
        {
            // `-v` removes anonymous volumes attached to the container. The postgres,
            // scylla, and cassandra images all declare a VOLUME for their data dir, so
            // docker auto-creates an anonymous volume per container even though bench mode
            // never explicitly mounts one. Without `-v` those anonymous volumes accumulate
            // across every reap cycle and slowly fill /var/lib/docker.
            await DockerAsync(["rm", "-f", "-v", name], ct).ConfigureAwait(false);
        }
    }

    private static async Task<int> DockerAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return -1;
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return proc.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}

/// <summary>Fixed container names for bench mode. Values match
/// <c>Interfold.AppHost.TestBenchContainerNames</c> — keep in sync.</summary>
public static class TestBenchContainerNames
{
    public const string Postgres = "interfold-test-bench-pg";
    public const string Scylla = "interfold-test-bench-scylla";
    public const string Cassandra = "interfold-test-bench-cassandra";

    public static readonly IReadOnlyList<string> All = [Postgres, Scylla, Cassandra];
}

