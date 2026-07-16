using System.Diagnostics;
using Interfold.Contracts.Configuration;

namespace Interfold.IntegrationTests.TestServices;

/// <summary>
/// Bounded Docker teardown for Aspire test containers. Resolves runtime container names via the
/// <c>aspire-resource-name</c> label (same discovery contract as
/// <c>Interfold.AppHost.DockerExecCqlProbe</c>) and issues <c>docker stop -t</c> before the
/// Aspire host's implicit dispose so slow Scylla graceful shutdown cannot stall the test process.
/// </summary>
internal static class AspireContainerCleanup
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public static async Task StopResourcesAsync(IEnumerable<string> resourceNames, CancellationToken cancellationToken = default)
    {
        foreach (var resourceName in resourceNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await StopResourceAsync(resourceName, cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task<int> CountRunningAspireContainersAsync(CancellationToken cancellationToken = default)
    {
        var (exit, stdout, _) = await RunDockerAsync(
            ["ps", "--filter", "label=aspire-resource-name", "--format", "{{.Names}}"],
            cancellationToken).ConfigureAwait(false);

        if (exit != 0 || string.IsNullOrWhiteSpace(stdout))
            return 0;

        return stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Count(line => !string.IsNullOrWhiteSpace(line));
    }

    public static string[] SharedDbResourceNames()
    {
        var names = new List<string> { ComposeServices.Postgres };
        if (RequiredFixtures.NeedScylla)
            names.Add(ComposeServices.ScyllaSingle);
        if (RequiredFixtures.NeedCassandra)
            names.Add(ComposeServices.Cassandra);
        return names.ToArray();
    }

    public static string[] MultiNodeScyllaResourceNames()
        => ComposeServices.ScyllaRegionalNodes;

    private static async Task StopResourceAsync(string resourceName, CancellationToken cancellationToken)
    {
        var (containerName, _) = await ResolveContainerNameAsync(resourceName, cancellationToken).ConfigureAwait(false);
        if (containerName is null)
            return;

        await RunDockerAsync(
            ["stop", "-t", StopTimeout.TotalSeconds.ToString("0"), containerName],
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(string? ContainerName, string? Error)> ResolveContainerNameAsync(
        string resourceName,
        CancellationToken cancellationToken)
    {
        var labelMatch = await RunDockerAsync(
            ["ps", "--filter", $"label=aspire-resource-name={resourceName}", "--format", "{{.Names}}"],
            cancellationToken).ConfigureAwait(false);

        if (labelMatch.Exit == 0 && !string.IsNullOrWhiteSpace(labelMatch.Stdout))
        {
            var first = labelMatch.Stdout.Trim()
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
                return (first, null);
        }

        var prefixMatch = await RunDockerAsync(
            ["ps", "--filter", $"name=^{resourceName}-", "--format", "{{.Names}}"],
            cancellationToken).ConfigureAwait(false);

        if (prefixMatch.Exit == 0 && !string.IsNullOrWhiteSpace(prefixMatch.Stdout))
        {
            var first = prefixMatch.Stdout.Trim()
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
                return (first, null);
        }

        return (null, $"no running container matches resource '{resourceName}'");
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunDockerAsync(
        IEnumerable<string> args,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var commandCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        commandCts.CancelAfter(CommandTimeout);

        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc is null)
                return (-1, string.Empty, "failed to spawn docker");

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(commandCts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(commandCts.Token);
            await proc.WaitForExitAsync(commandCts.Token).ConfigureAwait(false);
            return (proc.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (commandCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(proc);
            return (-1, string.Empty, $"docker command timed out after {CommandTimeout.TotalSeconds:0}s");
        }
        catch (Exception ex)
        {
            TryKill(proc);
            return (-1, string.Empty, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void TryKill(Process? proc)
    {
        if (proc is null || proc.HasExited)
            return;

        try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
    }
}
