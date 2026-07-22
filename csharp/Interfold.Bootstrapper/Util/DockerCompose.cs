using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Interfold.Bootstrapper.Cli;

namespace Interfold.Bootstrapper.Util;

internal static class DockerCompose
{
    public static async Task<ProcessRunResult> UpAsync(
        string composeFile,
        IReadOnlyList<string>? services = null,
        bool detach = true,
        bool build = false,
        CancellationToken ct = default)
    {
        var args = new List<string> { "compose", "-f", composeFile, "up" };
        if (detach) args.Add("-d");
        if (build) args.Add("--build");
        if (services is not null) args.AddRange(services);
        return await ProcessRunner.RunAsync("docker", args, ct: ct).ConfigureAwait(false);
    }

    public static async Task<ProcessRunResult> PullAsync(
        string composeFile,
        IReadOnlyList<string>? services = null,
        CancellationToken ct = default)
    {
        var args = new List<string> { "compose", "-f", composeFile, "pull" };
        if (services is not null) args.AddRange(services);
        return await ProcessRunner.RunAsync("docker", args, ct: ct).ConfigureAwait(false);
    }

    public static async Task<ProcessRunResult> StopAsync(
        string composeFile,
        IReadOnlyList<string>? services = null,
        CancellationToken ct = default)
    {
        var args = new List<string> { "compose", "-f", composeFile, "stop" };
        if (services is not null) args.AddRange(services);
        return await ProcessRunner.RunAsync("docker", args, ct: ct).ConfigureAwait(false);
    }

    /// <summary>Sister of <see cref="StopAsync"/>. Does NOT create containers — pair with
    /// <see cref="UpAsync"/> when the container may not exist yet.</summary>
    public static async Task<ProcessRunResult> StartAsync(
        string composeFile,
        IReadOnlyList<string>? services = null,
        CancellationToken ct = default)
    {
        var args = new List<string> { "compose", "-f", composeFile, "start" };
        if (services is not null) args.AddRange(services);
        return await ProcessRunner.RunAsync("docker", args, ct: ct).ConfigureAwait(false);
    }

    /// <summary><paramref name="removeVolumes"/> adds <c>-v</c> (destructive; operator opt-in).</summary>
    public static async Task<ProcessRunResult> DownAsync(
        string composeFile,
        IReadOnlyList<string>? services = null,
        bool removeVolumes = false,
        CancellationToken ct = default)
    {
        var args = new List<string> { "compose", "-f", composeFile, "down" };
        if (removeVolumes) args.Add("-v");
        if (services is not null) args.AddRange(services);
        return await ProcessRunner.RunAsync("docker", args, ct: ct).ConfigureAwait(false);
    }

    /// <summary>Defaults to <c>-q</c> (container id only). <paramref name="includeStopped"/>
    /// adds <c>-a</c> so stopped-but-created containers appear (restore-path requirement).</summary>
    public static async Task<ProcessRunResult> PsAsync(
        string composeFile,
        string? service = null,
        bool quietIds = true,
        bool includeStopped = false,
        CancellationToken ct = default)
    {
        var args = new List<string> { "compose", "-f", composeFile, "ps" };
        if (includeStopped) args.Add("-a");
        if (quietIds) args.Add("-q");
        if (service is not null) args.Add(service);
        return await ProcessRunner.RunAsync("docker", args, ct: ct).ConfigureAwait(false);
    }

    public static async Task<ProcessRunResult> LogsAsync(
        string composeFile,
        IReadOnlyList<string>? services = null,
        bool follow = false,
        int? tail = null,
        CancellationToken ct = default)
    {
        var args = new List<string> { "compose", "-f", composeFile, "logs" };
        if (follow) args.Add("-f");
        if (tail is not null) args.Add($"--tail={tail}");
        if (services is not null) args.AddRange(services);
        return await ProcessRunner.RunAsync("docker", args, ct: ct).ConfigureAwait(false);
    }

    /// <summary>One-liner for the "log intent → up → exit-check → log stdout" quad each phase
    /// re-implemented. Emits <see cref="PhaseLogger.PhaseFail"/> before throwing when both
    /// <paramref name="phase"/> and <paramref name="phaseFailReason"/> are supplied (Launch/Restore
    /// bring-ups skip PhaseFail here, so both stay optional).</summary>
    public static async Task UpCheckedAsync(
        string composeFile,
        IReadOnlyList<string>? services,
        PhaseLogger logger,
        CancellationToken ct,
        string? phase = null,
        string? phaseFailReason = null)
    {
        var svcLabel = services is { Count: > 0 } ? string.Join(' ', services) : "...";
        logger.Info($"    docker compose up -d {svcLabel}");
        var run = await UpAsync(composeFile, services, detach: true, build: false, ct: ct).ConfigureAwait(false);
        if (run.ExitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(run.StdErr)) logger.Error(run.StdErr.Trim());
            if (phase is not null && phaseFailReason is not null)
            {
                logger.PhaseFail(phase, phaseFailReason);
            }
            var svcSuffix = services is { Count: > 0 } ? $" for [{string.Join(", ", services)}]" : string.Empty;
            throw new InvalidOperationException(
                $"docker compose up -d{svcSuffix} exited with code {run.ExitCode}: {run.StdErr.Trim()}");
        }
        if (!string.IsNullOrWhiteSpace(run.StdOut)) logger.Info(run.StdOut.Trim());
    }

    public static Task<ProcessRunResult> ExecAsync(
        string composeFile,
        string service,
        string command,
        IReadOnlyList<string>? args = null,
        IReadOnlyDictionary<string, string>? env = null,
        string? stdin = null,
        CancellationToken ct = default)
    {
        return DockerComposeExec.RunAsync(composeFile, service, command, args, env, stdin, ct);
    }

    /// <summary>Container id for a compose service via <c>ps -q</c>. Empty when no container
    /// exists. <paramref name="includeStopped"/> matches stopped containers (restore path).</summary>
    public static async Task<string> ResolveContainerIdAsync(
        string composeFile, string service, bool includeStopped = false, CancellationToken ct = default)
    {
        var run = await PsAsync(composeFile, service, includeStopped: includeStopped, ct: ct).ConfigureAwait(false);
        if (run.ExitCode != 0 || string.IsNullOrWhiteSpace(run.StdOut)) return string.Empty;
        return run.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
    }

    /// <summary>Shared non-empty guard for the <c>docker cp</c> argv builders.</summary>
    public static void ValidateContainerCpParams(string containerId, string dataPath)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            throw new ArgumentException("containerId must be non-empty.", nameof(containerId));
        }
        if (string.IsNullOrWhiteSpace(dataPath))
        {
            throw new ArgumentException("dataPath must be non-empty.", nameof(dataPath));
        }
    }

    /// <summary>Shared preamble for admin-role exec into the postgres container. Password flows
    /// via <c>PGPASSWORD</c> env, never argv, so it can't leak to <c>ps</c>/audit. <paramref
    /// name="toolTrailer"/> is appended verbatim in the order the underlying binary expects.</summary>
    internal static IReadOnlyList<string> BuildPostgresExecArgs(
        string composeFile, string service, string tool, string adminUser, string database, params string[] toolTrailer)
    {
        var argv = new List<string>(11 + (toolTrailer?.Length ?? 0))
        {
            "compose", "-f", composeFile,
            "exec", "-T",
            "--env", DatabaseArchiveStreamer.PgPasswordEnvVar,
            service,
            tool,
            "-U", adminUser,
            "-d", database,
        };
        if (toolTrailer is not null && toolTrailer.Length > 0)
        {
            argv.AddRange(toolTrailer);
        }
        return argv;
    }

    /// <summary><c>docker cp &lt;id&gt;:&lt;path&gt; -</c>: streams a container path to host
    /// stdout as raw tar. Backup path.</summary>
    internal static IReadOnlyList<string> BuildContainerCpFromContainer(string containerId, string containerPath)
    {
        ValidateContainerCpParams(containerId, containerPath);
        return ["cp", $"{containerId}:{containerPath}", "-"];
    }

    /// <summary>Mirror of <see cref="BuildContainerCpFromContainer"/> — restore path.</summary>
    internal static IReadOnlyList<string> BuildContainerCpIntoContainer(string containerId, string containerPath)
    {
        ValidateContainerCpParams(containerId, containerPath);
        return ["cp", "-", $"{containerId}:{containerPath}"];
    }
}
