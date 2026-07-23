using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;
using Interfold.Shared.Contracts.Configuration;

namespace Interfold.Bootstrapper.Phases;

/// <summary>Inverse of <see cref="BackupPhase"/>. Postgres restores in place via
/// <c>pg_restore --clean --if-exists</c>. Scylla stops the API/web tier, stops the seed,
/// wipes and re-hydrates <c>/var/lib/scylla</c> via <c>docker cp -</c>, and restarts.
/// Destructive; gated by interactive confirmation or
/// <see cref="BootstrapOptions.RestoreForce"/>.</summary>
internal static class RestorePhase
{
    private static readonly string Phase = BootstrapCommand.Restore.ToPhaseLogName();

    /// <summary>Client services stopped before the scylla restore and restarted after.</summary>
    internal static readonly string[] ScyllaRestoreClientServices = [ComposeServices.InterfoldApi, ComposeServices.OctoconWeb];

    public static async Task<int> RunAsync(BootstrapOptions options, PhaseLogger logger, CancellationToken ct)
    {
        logger.PhaseStart(Phase);

        var config = await PhaseArtifactLoader
            .LoadRequiredConfigAsync(options, logger, Phase, "restore", ct)
            .ConfigureAwait(false);

        var secrets = PhaseArtifactLoader.LoadRequiredSecretsOrFail(options, logger, Phase, "Restore");

        var composeFile = PhaseArtifactLoader.RequireComposeFileOrFail(options, logger, Phase);

        var backupRoot = BackupPhase.ResolveBackupRoot(options, config);
        var (postgresArchive, scyllaArchive) = ResolveArchives(options, backupRoot, logger);
        if (postgresArchive is null && scyllaArchive is null)
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.NoArchives);
            throw new InvalidOperationException(
                "restore requires at least one of --restore-postgres, --restore-scylla, or --restore-latest " +
                $"(with archives under {backupRoot}/).");
        }

        // Non-interactive callers MUST pass --force so a stray systemd unit or CI job
        // can't wipe a data volume.
        if (!options.RestoreForce)
        {
            if (options.NonInteractive || Console.IsInputRedirected)
            {
                logger.PhaseFail(Phase, PhaseFailureReasons.ConfirmationRequired);
                throw new InvalidOperationException(
                    "restore is destructive and requires --force in non-interactive mode. " +
                    "Re-run interactively without --non-interactive to type 'y' at the confirmation prompt, " +
                    "or pass --force to skip the prompt.");
            }
            Console.WriteLine();
            Console.WriteLine("*** DESTRUCTIVE OPERATION ***");
            Console.WriteLine("This will overwrite the current database contents with the archive on disk.");
            if (postgresArchive is not null) Console.WriteLine($"  postgres <- {postgresArchive}");
            if (scyllaArchive is not null) Console.WriteLine($"  scylla   <- {scyllaArchive}");
            Console.Write("Type 'y' to proceed, anything else to abort: ");
            var answer = Console.ReadLine();
            if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                logger.PhaseSkip(Phase, PhaseFailureReasons.Skip.OperatorAborted);
                return 0;
            }
        }

        if (postgresArchive is not null)
        {
            await RestorePostgresAsync(composeFile, postgresArchive, config, secrets, logger, ct).ConfigureAwait(false);
        }

        if (scyllaArchive is not null)
        {
            await RestoreScyllaAsync(composeFile, scyllaArchive, config, logger, ct).ConfigureAwait(false);
        }

        logger.PhaseDone(Phase);
        return 0;
    }

    /// <summary>Explicit paths beat <c>--restore-latest</c>; latest only fills components
    /// left unnamed. Returns (null, null) when neither component was chosen.</summary>
    internal static (string? PostgresArchive, string? ScyllaArchive) ResolveArchives(
        BootstrapOptions options, string backupRoot, PhaseLogger logger)
    {
        string? postgres = options.RestorePostgresArchive;
        string? scylla = options.RestoreScyllaArchive;

        if (options.RestoreLatest)
        {
            if (postgres is null)
            {
                postgres = BackupStoragePaths.LatestFile(Path.Combine(backupRoot, BackupStoragePaths.PostgresDir), BackupStoragePaths.PostgresArchivePattern)?.FullName;
                if (postgres is not null) logger.Info($"    resolved --restore-latest postgres: {postgres}");
            }
            if (scylla is null)
            {
                scylla = BackupStoragePaths.LatestFile(Path.Combine(backupRoot, BackupStoragePaths.ScyllaDir), BackupStoragePaths.ScyllaArchivePattern)?.FullName;
                if (scylla is not null) logger.Info($"    resolved --restore-latest scylla: {scylla}");
            }
        }

        if (postgres is not null && !File.Exists(postgres))
        {
            throw new InvalidOperationException($"Postgres archive not found: {postgres}");
        }
        if (scylla is not null && !File.Exists(scylla))
        {
            throw new InvalidOperationException($"Scylla archive not found: {scylla}");
        }
        return (postgres, scylla);
    }

    /// <summary>pg_restore argv. <c>--clean --if-exists</c> keeps re-runs idempotent;
    /// <c>--single-transaction</c> keeps a partial failure from leaving a half-restored DB.</summary>
    internal static IReadOnlyList<string> BuildPgRestoreArgs(
        string composeFile, string adminUser, string database)
        => DockerCompose.BuildPostgresExecArgs(
            composeFile, ComposeServices.Postgres, "pg_restore", adminUser, database,
            "--clean", "--if-exists",
            "--single-transaction",
            "--no-owner");

    /// <summary><c>docker cp - &lt;id&gt;:&lt;path&gt;</c> argv: <c>-</c> reads a tar from
    /// stdin into the container. Mirror of <see cref="BackupPhase.BuildContainerCpArgs"/>.</summary>
    internal static IReadOnlyList<string> BuildContainerCpWriteArgs(string containerId, string dataPath)
        => DockerCompose.BuildContainerCpIntoContainer(containerId, dataPath);

    private static async Task RestorePostgresAsync(
        string composeFile, string archivePath, BootstrapConfig config, GeneratedSecrets secrets,
        PhaseLogger logger, CancellationToken ct)
    {
        var (adminUser, adminPassword) = PhaseArtifactLoader.RequireAdminPassword(secrets, logger, Phase);

        // Idempotent — update-images rollback may have left the stack stopped.
        await DockerCompose.UpCheckedAsync(composeFile, [ComposeServices.Postgres], logger, ct).ConfigureAwait(false);
        await WaitForPostgresAsync(composeFile, logger, ct).ConfigureAwait(false);

        logger.Info($"    postgres: pg_restore --clean --if-exists <- {archivePath}");
        var argv = BuildPgRestoreArgs(composeFile, adminUser, config.PostgresDatabase);
        await DatabaseArchiveStreamer.StreamFileToProcessStdinAsync(
            "docker", argv,
            environment: DatabaseArchiveStreamer.PgPasswordEnv(adminPassword),
            sourcePath: archivePath,
            decompress: false,
            ct: ct).ConfigureAwait(false);
        logger.Info("    postgres: restore complete");
    }

    private static async Task RestoreScyllaAsync(
        string composeFile, string archivePath, BootstrapConfig config,
        PhaseLogger logger, CancellationToken ct)
    {
        var (service, dataPath) = BackupPhase.ResolveScyllaSeed(config);

        // Stop clients so hydration queries don't race the scylla stop. Best-effort:
        // missing services (cassandra-mode without web) get a warning, not a failure.
        foreach (var client in ScyllaRestoreClientServices)
        {
            var stop = await DockerCompose.StopAsync(composeFile, [client], ct: ct).ConfigureAwait(false);
            if (stop.ExitCode != 0)
            {
                logger.Warn($"docker compose stop {client} exited {stop.ExitCode} (missing service?): {stop.StdErr.Trim()}");
            }
        }

        // Release file locks before overwriting the data volume.
        logger.Info($"    scylla: stopping {service}");
        await PhaseRunner.RunOrPhaseFailAsync(
            () => DockerCompose.StopAsync(composeFile, [service], ct: ct),
            logger, Phase, PhaseFailureReasons.StopScylla,
            $"docker compose stop {service}", ct).ConfigureAwait(false);

        // docker cp needs a container (stopped is fine); fresh boxes need one created first.
        var containerId = await DockerCompose.ResolveContainerIdAsync(composeFile, service, includeStopped: true, ct: ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(containerId))
        {
            // `compose up --no-start` creates without running on every compose v2 version.
            await PhaseRunner.RunOrPhaseFailAsync(
                () => ProcessRunner.RunAsync("docker",
                    ["compose", "-f", composeFile, "up", "--no-start", service], ct: ct),
                logger, Phase, PhaseFailureReasons.CreateScyllaContainer,
                $"docker compose up --no-start {service}", ct).ConfigureAwait(false);
            containerId = await DockerCompose.ResolveContainerIdAsync(composeFile, service, includeStopped: true, ct: ct).ConfigureAwait(false);
        }
        if (string.IsNullOrEmpty(containerId))
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.ResolveScyllaContainer);
            throw new InvalidOperationException(
                $"Failed to resolve container id for compose service '{service}' even after create.");
        }

        // `docker cp -` overlays; leftover SSTables from a failed prior run would confuse
        // startup. `docker exec` needs a running container, so we wipe via a short-lived
        // alpine helper mounted with `--volumes-from` (the portable way to mutate a stopped
        // container's volume without knowing the host mountpoint).
        logger.Info($"    scylla: wiping {dataPath} inside container");
        var wipe = await ProcessRunner.RunAsync("docker",
            ["run", "--rm", "--volumes-from", containerId, "alpine:3.20", "sh", "-c",
             $"rm -rf {dataPath}/* {dataPath}/.[!.]* 2>/dev/null || true"],
            ct: ct).ConfigureAwait(false);
        if (wipe.ExitCode != 0)
        {
            // Some rootless-docker setups unmount oddly; docker cp still overlays.
            logger.Warn($"scylla wipe helper exited {wipe.ExitCode}: {wipe.StdErr.Trim()}");
        }

        logger.Info($"    scylla: docker cp - {service}({containerId[..Math.Min(12, containerId.Length)]}):{dataPath}");
        var cpArgs = BuildContainerCpWriteArgs(containerId, dataPath);
        await DatabaseArchiveStreamer.StreamFileToProcessStdinAsync(
            "docker", cpArgs,
            environment: null,
            sourcePath: archivePath,
            decompress: true,
            ct: ct).ConfigureAwait(false);

        // Startup re-hydrates from the restored SSTables — no explicit reload needed.
        logger.Info($"    scylla: starting {service}");
        await PhaseRunner.RunOrPhaseFailAsync(
            () => DockerCompose.StartAsync(composeFile, [service], ct: ct),
            logger, Phase, PhaseFailureReasons.StartScylla,
            $"docker compose start {service}", ct).ConfigureAwait(false);

        // Order matters: API first so the web tier's health probe finds a live upstream.
        foreach (var client in ScyllaRestoreClientServices)
        {
            var start = await DockerCompose.StartAsync(composeFile, [client], ct: ct).ConfigureAwait(false);
            if (start.ExitCode != 0)
            {
                logger.Warn($"docker compose start {client} exited {start.ExitCode} (missing service?): {start.StdErr.Trim()}");
            }
        }

        logger.Info("    scylla: restore complete");
    }

    private static Task WaitForPostgresAsync(
        string composeFile, PhaseLogger logger, CancellationToken ct)
    {
        // Cluster is already warm; the DatabaseInitPhase 3-in-a-row check would cost ~4s
        // for no real safety gain, so the first pg_isready is the handoff signal.
        return Util.PostgresReadinessProbe.WaitAsync(
            composeFile,
            ComposeServices.Postgres,
            TimeSpan.FromMinutes(10),
            logger,
            ct,
            runAsRole: Interfold.Shared.Contracts.Configuration.PostgresRoles.Init);
    }

}


