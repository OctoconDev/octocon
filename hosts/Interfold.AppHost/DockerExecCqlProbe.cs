using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Interfold.AppHost;

/// <summary>Runs a readiness probe against a Scylla container via <c>docker exec</c>. Feeds
/// the per-node <c>{name}-cql</c> health checks in <see cref="InterfoldAppHost.Configure"/>
/// so each node's start gates on the previous node's CQL listener actually accepting
/// traffic. Credentials stay in the container's env (CQLSH_USER/CQLSH_PASSWORD) so no
/// secrets flow through the AppHost's argv. Container name is resolved per-probe by
/// <c>docker ps</c> filter — the DCP side-channel doesn't fire reliably under
/// Aspire.Hosting.Testing's in-process host (see multinode-stagger-debug.log).</summary>
internal static class DockerExecCqlProbe
{
    // 10s is a comfortable ceiling for the slowest CI: docker exec cold-start (~50–200ms)
    // + cqlsh handshake (~500–1500ms) + nodetool fallback of the same order.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    // Single $ is correct here (docker exec bypasses compose YAML — the compose-level probe
    // uses $$ to escape past compose's own interpolation pass).
    private const string ReadinessScript =
        $"cqlsh -u \"${ContainerEnvNames.CqlshUser}\" -p \"${ContainerEnvNames.CqlshPassword}\" -e 'DESCRIBE CLUSTER' >/dev/null 2>&1 " +
        "|| nodetool status | grep -q '^UN'";

    /// <summary>Resolves the runtime container name for <paramref name="resourceName"/>
    /// and runs the probe. Returns Unhealthy while the container hasn't been allocated yet
    /// (Aspire retries on its polling interval).</summary>
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
        // DCP stamps `aspire-resource-name=<logical>` on every container (stable across
        // Aspire 9.x / 13.x). Prefix match is the fallback for orchestrator builds that don't.
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
            return HealthCheckResult.Unhealthy(stderr);
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
        try { proc.Kill(entireProcessTree: true); } catch { }
    }
}
