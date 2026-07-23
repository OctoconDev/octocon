using System.Diagnostics;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// One-shot host-kernel <c>fs.aio-max-nr</c> tuner used before any test fixture starts a
/// Scylla container. Mirrors the dynamic formula in
/// <c>scripts/docker/ensure-host-aio.sh</c> and
/// <c>Interfold.Bootstrapper/Phases/PrerequisitesPhase.cs</c>: per-Scylla-node Seastar AIO
/// budget × node count, plus a fixed headroom for non-Scylla AIO consumers (Postgres etc.).
/// </summary>
/// <remarks>
/// <para>
/// On a Linux host the test process can write to <c>/proc/sys/fs/aio-max-nr</c> directly when
/// run as root. On Docker Desktop (Mac / Windows) the test process runs on the host OS but
/// the kernel sysctl lives inside the Docker VM, so direct writes don't work. To cover both
/// cases uniformly we always go through a one-shot privileged Alpine container — that way
/// the same code path works for developer-laptop runs, GitHub Actions Linux runners, and
/// Docker-in-Docker bootstrapper integration tests.
/// </para>
/// <para>
/// Idempotent and process-static: once we've raised the limit during a session we don't
/// re-spawn the helper container for subsequent fixture inits. The helper itself is also
/// idempotent (the script never lowers the limit), so even without the cache a re-run would
/// be safe — the cache just avoids the docker-spawn cost.
/// </para>
/// </remarks>
internal static class HostAioPrerequisite
{
    /// <summary>Per-Scylla-node Seastar minimum (refusing-to-start threshold) per the Seastar startup error.</summary>
    private const int PerNodeMin = 66_563;

    /// <summary>Per-Scylla-node Seastar recommended budget (optimal networking performance).</summary>
    private const int PerNodeRecommended = 116_562;

    /// <summary>Headroom for non-Scylla AIO consumers (Postgres, libraries, etc.).</summary>
    private const int Headroom = 50_000;

    /// <summary>Image used for the privileged sysctl write. Pinned so a registry outage doesn't break tests silently.</summary>
    private const string SysctlImage = "alpine:3.20";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static int _appliedFor;

    /// <summary>
    /// Number of Scylla nodes <see cref="MultiNodeScyllaFixture"/> spins up when active.
    /// Mirrors the regions array in <c>InterfoldAppHost.cs</c>; if you ever change that
    /// region list, update this too — there's a small build-time cross-check via the array
    /// length comparison in <see cref="MultiNodeScyllaFixture.WaitForResourcesAsync"/>.
    /// </summary>
    private const int MultiNodeNodeCount = 7;

    /// <summary>
    /// Total Scylla nodes the current test session will run concurrently across all
    /// fixtures, derived from <see cref="RequiredFixtures"/>. SharedDbFixture's single-node
    /// Scylla counts as 1; the multi-DC fixture adds <see cref="MultiNodeNodeCount"/>.
    /// Cassandra is intentionally excluded — it doesn't use Seastar's AIO budget.
    /// </summary>
    public static int TotalScyllaNodesForSession()
        => (RequiredFixtures.NeedScylla ? 1 : 0) +
           (RequiredFixtures.NeedMultiNodeScylla ? MultiNodeNodeCount : 0);

    /// <summary>
    /// Raises the host's <c>fs.aio-max-nr</c> to satisfy the requested Scylla node count if
    /// the current value is below the minimum. A no-op when <paramref name="scyllaNodeCount"/>
    /// is zero (Cassandra-only or InMemory-only sessions).
    /// </summary>
    /// <param name="scyllaNodeCount">Total Scylla nodes the session will run concurrently across all fixtures.</param>
    /// <param name="ct">Cancellation token honored by the helper docker invocations.</param>
    public static async Task EnsureAsync(int scyllaNodeCount, CancellationToken ct = default)
    {
        if (scyllaNodeCount <= 0)
        {
            return;
        }

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-application is harmless (the privileged container only ever raises the limit)
            // but spawning Alpine for every fixture init in a multi-fixture session adds 1-2s
            // of avoidable churn. Cache by the highest count seen so far so a session that
            // first runs SharedDbFixture (1 node) then MultiNodeScyllaFixture (+7 nodes) gets
            // the helper invoked twice — once at 1, then re-invoked for the larger 8-node
            // requirement.
            if (_appliedFor >= scyllaNodeCount)
            {
                return;
            }

            var minRequired = scyllaNodeCount * PerNodeMin + Headroom;
            var target = scyllaNodeCount * PerNodeRecommended + Headroom;

            // Inline sh script so we don't have to bind-mount ensure-host-aio.sh from the test
            // project. The logic is identical: read current, compare to min, write target if
            // below. Output goes through Console.Error so the operator sees what happened
            // even when the test framework swallows stdout.
            var inline =
                $"current=$(cat /proc/sys/fs/aio-max-nr); " +
                $"if [ \"$current\" -ge {minRequired} ]; then " +
                $"  echo \"[host-aio] current=$current >= min={minRequired} for {scyllaNodeCount} Scylla node(s); ok\" 1>&2; " +
                $"else " +
                $"  echo \"[host-aio] raising current=$current to {target} for {scyllaNodeCount} Scylla node(s)\" 1>&2; " +
                $"  echo {target} > /proc/sys/fs/aio-max-nr; " +
                $"fi";

            var args = new[]
            {
                "run", "--rm", "--privileged",
                SysctlImage,
                "sh", "-c", inline,
            };

            var psi = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to launch `docker run` for host AIO tuning.");

            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdErr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            var stdOut = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);

            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Host AIO prerequisite failed (docker exit={proc.ExitCode}). " +
                    $"Required min={minRequired} for {scyllaNodeCount} Scylla node(s). " +
                    $"Set it manually with `sudo sysctl -w fs.aio-max-nr={target}` or run `bash scripts/docker/ensure-host-aio.sh --scylla-nodes {scyllaNodeCount}`. " +
                    $"stderr: {stdErr.Trim()} stdout: {stdOut.Trim()}");
            }

            _appliedFor = scyllaNodeCount;
        }
        finally
        {
            Gate.Release();
        }
    }
}
