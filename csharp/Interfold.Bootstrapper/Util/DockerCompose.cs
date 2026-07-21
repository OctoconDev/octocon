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

    /// <summary>
    /// Starts previously created compose services. Sister of <see cref="StopAsync"/> — the
    /// "start what we stopped" leg of a stop/start pair. Does NOT create containers; pair
    /// with <see cref="UpAsync"/> when the container may not exist yet.
    /// </summary>
    public static async Task<ProcessRunResult> StartAsync(
        string composeFile,
        IReadOnlyList<string>? services = null,
        CancellationToken ct = default)
    {
        var args = new List<string> { "compose", "-f", composeFile, "start" };
        if (services is not null) args.AddRange(services);
        return await ProcessRunner.RunAsync("docker", args, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <c>docker compose down</c>. When <paramref name="removeVolumes"/> is true,
    /// adds <c>-v</c> so named volumes are also removed (destructive — only reachable via
    /// operator opt-in paths).
    /// </summary>
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

    /// <summary>
    /// Runs <c>docker compose ps</c>. Defaults to <c>-q</c> (container id only), which is
    /// what every current caller wants (they parse stdout for the id and pipe it to
    /// <c>docker cp</c> / <c>docker inspect</c>). Set <paramref name="includeStopped"/> to
    /// add <c>-a</c> so stopped-but-created containers are also returned — required for the
    /// restore path which needs a handle on a container it just <c>stop</c>'d.
    /// </summary>
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

    /// <summary>
    /// One-liner replacement for the "log intent → <see cref="UpAsync"/> → check exit code
    /// → log stdout on success" quad every phase used to hand-roll. Optionally emits
    /// <see cref="PhaseLogger.PhaseFail"/> before throwing when both <paramref name="phase"/>
    /// and <paramref name="phaseFailReason"/> are supplied (LaunchPhase and RestorePhase's
    /// bring-ups deliberately DO NOT emit PhaseFail here, so both stay optional).
    /// </summary>
    /// <remarks>
    /// Stack layering rule — <see cref="DockerCompose"/> normally stays a pure transport
    /// helper (see the class-level architecture note upstream). This exception is
    /// justified because every current caller (DatabaseInitPhase, LaunchPhase, RestorePhase,
    /// UpdateImagesPhase) rebuilds the same four-line envelope byte-identically, and pulling
    /// it into a caller-side base class would drag five phases into an inheritance hierarchy
    /// they don't otherwise need. Keep this helper minimal — if a phase grows a bespoke
    /// exit-code recovery path, it should inline <see cref="UpAsync"/> instead of overriding.
    /// </remarks>
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

    /// <summary>
    /// Resolves the runtime container ID for a compose service via <c>docker compose ps -q</c>.
    /// Returns <see cref="string.Empty"/> when the service has no container (not created,
    /// or <c>ps</c> failed). Set <paramref name="includeStopped"/> to also match containers
    /// that exist but are not running (needed by the restore path after <c>stop</c>).
    /// </summary>
    public static async Task<string> ResolveContainerIdAsync(
        string composeFile, string service, bool includeStopped = false, CancellationToken ct = default)
    {
        var run = await PsAsync(composeFile, service, includeStopped: includeStopped, ct: ct).ConfigureAwait(false);
        if (run.ExitCode != 0 || string.IsNullOrWhiteSpace(run.StdOut)) return string.Empty;
        return run.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
    }

    /// <summary>
    /// Validates <paramref name="containerId"/> and <paramref name="dataPath"/> are non-empty.
    /// Shared guard for <c>docker cp</c> argv builders in <see cref="Phases.BackupPhase"/>
    /// and <see cref="Phases.RestorePhase"/>.
    /// </summary>
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

    /// <summary>
    /// Builds the shared <c>docker compose exec -T --env PGPASSWORD msg-db &lt;tool&gt; -U &lt;user&gt; -d &lt;db&gt; …</c>
    /// argv preamble used by every phase that shells into the postgres container as an admin
    /// role (pg_dump, pg_restore, pg_isready-with-creds, ad-hoc DDL). The password lands on the
    /// exec'd process via the <c>PGPASSWORD</c> env var — never on argv — so the value never
    /// leaks to <c>ps</c> or audit logs.
    /// <para>
    /// <paramref name="toolTrailer"/> is appended verbatim after the base argv (
    /// <c>-Fc</c> for pg_dump, <c>--clean --if-exists --single-transaction --no-owner</c> for
    /// pg_restore, etc.). Callers pass the tool-specific flags in the order the underlying
    /// binary expects them.
    /// </para>
    /// </summary>
    /// <remarks>
    /// The service name is hardcoded to <see cref="Configuration.ComposeServices.Postgres"/>
    /// (via the caller's constant) rather than exposed as a parameter — every current
    /// callsite targets the same service, and letting the caller pass one would invite the
    /// argv shape to drift as new tools are added. If a future phase needs to exec into a
    /// non-postgres service under this same shape, promote the service to a parameter here.
    /// </remarks>
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

    /// <summary>
    /// Builds the <c>docker cp &lt;id&gt;:&lt;path&gt; -</c> argv that streams a container path
    /// to the host as a raw tar archive on stdout. Callers pipe the emitted stream through a
    /// host-side compressor. Used by <see cref="Phases.BackupPhase"/>.
    /// </summary>
    internal static IReadOnlyList<string> BuildContainerCpFromContainer(string containerId, string containerPath)
    {
        ValidateContainerCpParams(containerId, containerPath);
        return ["cp", $"{containerId}:{containerPath}", "-"];
    }

    /// <summary>
    /// Builds the <c>docker cp - &lt;id&gt;:&lt;path&gt;</c> argv that streams a host-side tar
    /// payload from stdin back into a container path. Mirror of
    /// <see cref="BuildContainerCpFromContainer"/>. Used by <see cref="Phases.RestorePhase"/>.
    /// </summary>
    internal static IReadOnlyList<string> BuildContainerCpIntoContainer(string containerId, string containerPath)
    {
        ValidateContainerCpParams(containerId, containerPath);
        return ["cp", "-", $"{containerId}:{containerPath}"];
    }
}
