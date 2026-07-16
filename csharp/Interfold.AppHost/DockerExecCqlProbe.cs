using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Interfold.AppHostGraph;

/// <summary>
/// Runs a single readiness probe against a running Scylla container via <c>docker exec</c>.
/// Used by the per-node <c>{name}-cql</c> <see cref="IHealthCheck"/> registrations in
/// <see cref="InterfoldAppHost.Configure"/> to gate one node's start on the previous node's CQL
/// listener actually accepting traffic.
/// </summary>
/// <remarks>
/// <para>
/// The probe is a shell one-liner: first try authenticated <c>cqlsh DESCRIBE CLUSTER</c> using
/// the credentials the container already has in <c>CQLSH_USER</c>/<c>CQLSH_PASSWORD</c>; if that
/// fails (typical pre-bootstrap state, before <c>DatabaseInitPhase</c> has minted the app role),
/// fall back to <c>nodetool status | grep '^UN'</c>. The fallback covers the window between
/// "Scylla started" and "the auth bootstrap landed", which is exactly the window during which
/// non-seed nodes are joining the cluster. Same shape as the compose-level healthcheck at
/// <c>InterfoldAppHost.cs</c> (search for <c>service.Healthcheck</c>) so the orchestration-time
/// gate and the operator's runtime compose healthcheck stay in lockstep.
/// </para>
/// <para>
/// Credentials are intentionally <em>not</em> threaded through C# — they live as env vars on
/// the container itself, and the inner shell expands them. That keeps secrets out of the
/// AppHost's argv and matches the in-container interpolation pattern operators already see in
/// the generated compose file.
/// </para>
/// <para>
/// Container resolution: rather than tracking DCP-assigned container names via a side-channel
/// registry (which doesn't fire reliably under <c>Aspire.Hosting.Testing</c>'s in-process host —
/// observed in <c>artifacts/multinode-stagger-debug.log</c>, where only the first of two
/// nested AppHosts ever ran its <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>),
/// each invocation shells <c>docker ps --filter label=aspire-resource-name=&lt;resource&gt;</c>.
/// This is stateless, works identically for <c>aspire run</c> and the testing host, and is fast
/// enough for the 30 s health-check polling interval.
/// </para>
/// </remarks>
internal static class DockerExecCqlProbe
{
    /// <summary>
    /// Probe timeout. Each <c>docker exec</c> invocation costs ~50-200ms of cold-start overhead,
    /// the <c>cqlsh</c> connect handshake adds another ~500-1500ms, and the <c>nodetool</c>
    /// fallback adds a similar amount. 10 s leaves comfortable headroom for the slowest CI hosts
    /// while still failing fast enough that Aspire's health-check publisher can keep its
    /// polling cadence (default 30 s).
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The in-container readiness script. The doubled <c>$$</c> in the compose-level healthcheck
    /// at <see cref="InterfoldAppHost"/> is a compose-yaml escape; here we shell <c>docker exec</c>
    /// directly, so single <c>$</c> is correct.
    /// </summary>
    private const string ReadinessScript =
        $"cqlsh -u \"${ContainerEnvNames.CqlshUser}\" -p \"${ContainerEnvNames.CqlshPassword}\" -e 'DESCRIBE CLUSTER' >/dev/null 2>&1 " +
        "|| nodetool status | grep -q '^UN'";

    /// <summary>
    /// Resolves the runtime Docker container name for an Aspire resource and runs the readiness
    /// probe against it. Returns Unhealthy if the container hasn't been allocated yet (Aspire
    /// retries on its polling interval) or if the probe itself fails.
    /// </summary>
    /// <param name="resourceName">
    /// The logical Aspire resource name (e.g. <c>scylla-nam</c>). The runtime Docker container
    /// inherits this name + a unique <c>-{suffix}</c>; we resolve the suffix by listing
    /// containers filtered by the <c>aspire-resource-name=&lt;resourceName&gt;</c> label DCP
    /// emits, falling back to a name-prefix filter for older orchestrator builds that don't
    /// stamp the label.
    /// </param>
    /// <param name="cancellationToken">Cancellation propagated from Aspire's health-check publisher.</param>
    public static async Task<HealthCheckResult> RunAsync(string resourceName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            return HealthCheckResult.Unhealthy("resource name is empty");
        }

        var (containerName, lookupError) = await ResolveContainerNameAsync(resourceName, cancellationToken).ConfigureAwait(false);
        if (containerName is null)
        {
            return HealthCheckResult.Unhealthy(lookupError ?? "container not yet allocated");
        }

        return await RunExecProbeAsync(containerName, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(string? ContainerName, string? Error)> ResolveContainerNameAsync(string resourceName, CancellationToken cancellationToken)
    {
        // Aspire's DCP labels every container with `aspire-resource-name=<logical>`. The label
        // is stable across Aspire 9.x / 13.x, so we try that first. If a future orchestrator
        // version stops stamping it we transparently fall back to a name prefix match.
        var labelMatch = await DockerPsAsync(
            ["--filter", $"label=aspire-resource-name={resourceName}", "--format", "{{.Names}}"],
            cancellationToken).ConfigureAwait(false);
        if (labelMatch.Exit == 0 && !string.IsNullOrWhiteSpace(labelMatch.Stdout))
        {
            var first = labelMatch.Stdout.Trim().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
            {
                return (first, null);
            }
        }

        var prefixMatch = await DockerPsAsync(
            ["--filter", $"name=^{resourceName}-", "--format", "{{.Names}}"],
            cancellationToken).ConfigureAwait(false);
        if (prefixMatch.Exit == 0 && !string.IsNullOrWhiteSpace(prefixMatch.Stdout))
        {
            var first = prefixMatch.Stdout.Trim().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
            {
                return (first, null);
            }
        }

        if (labelMatch.Exit != 0)
        {
            return (null, $"docker ps (label) exit={labelMatch.Exit}: {labelMatch.Stderr.Trim()}");
        }
        if (prefixMatch.Exit != 0)
        {
            return (null, $"docker ps (prefix) exit={prefixMatch.Exit}: {prefixMatch.Stderr.Trim()}");
        }
        return (null, $"no running container matches resource '{resourceName}'");
    }

    private static async Task<HealthCheckResult> RunExecProbeAsync(string containerName, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(containerName);
        psi.ArgumentList.Add("sh");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(ReadinessScript);

        var (exit, stdout, stderr) = await RunDockerProcessAsync(psi, cancellationToken).ConfigureAwait(false);
        if (exit == 0)
        {
            return HealthCheckResult.Healthy();
        }
        if (exit == -1)
        {
            return HealthCheckResult.Unhealthy(stderr); // timeout / spawn failure
        }

        var detail = stderr.Length > 0 ? stderr : stdout;
        var trimmed = detail.Length > 256 ? detail[..256] : detail;
        return HealthCheckResult.Unhealthy($"docker exec exit={exit}: {trimmed.Trim()}");
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> DockerPsAsync(
        IEnumerable<string> args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("ps");
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        return await RunDockerProcessAsync(psi, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunDockerProcessAsync(
        ProcessStartInfo psi, CancellationToken cancellationToken)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(ProbeTimeout);

        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc is null)
            {
                return (-1, string.Empty, "failed to spawn docker");
            }

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(probeCts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(probeCts.Token);
            await proc.WaitForExitAsync(probeCts.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return (proc.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (probeCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(proc);
            return (-1, string.Empty, $"docker probe timed out after {ProbeTimeout.TotalSeconds:F0}s");
        }
        catch (Exception ex)
        {
            TryKill(proc);
            return (-1, string.Empty, $"docker probe failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void TryKill(Process? proc)
    {
        if (proc is null || proc.HasExited)
        {
            return;
        }
        try { proc.Kill(entireProcessTree: true); } catch { /* best-effort cleanup */ }
    }
}
