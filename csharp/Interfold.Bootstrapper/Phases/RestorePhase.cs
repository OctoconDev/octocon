using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;
using Interfold.Contracts.Configuration;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// One-shot database restore from backup archives on disk. The inverse of
/// <see cref="BackupPhase"/> in argv shape and stack coordination. Postgres restores in-place
/// via <c>pg_restore --clean --if-exists</c> against the running compose-exec endpoint;
/// Scylla restores by stopping the API/web tier, stopping the seed container, streaming the
/// tar.gz archive back into <c>/var/lib/scylla</c> via <c>docker cp -</c>, and starting the
/// stack. Destructive: gated by an interactive confirmation prompt (or
/// <see cref="BootstrapOptions.RestoreForce"/> in non-interactive mode).
/// </summary>
/// <remarks>
/// <para>
/// The postgres path uses <c>--clean --if-exists</c> so a partial-restore that hit an
/// error mid-way through can be re-run cleanly against the same live database — the
/// <c>DROP ... IF EXISTS</c> statements pg_restore emits during clean mode make the
/// operation idempotent.
/// </para>
/// <para>
/// The scylla path is heavier because the SSTable layout is filesystem-backed rather
/// than transactional: any running scylla process holds file locks and caches that
/// would poison a hot swap of the data volume. We stop the process, replace the
/// contents of <c>/var/lib/scylla</c> (via a wipe + `docker cp` re-hydrate that
/// mirrors the backup path's `docker cp` extract), and restart. The API and web tiers
/// are stopped first so hydration queries don't race the scylla stop.
/// </para>
/// </remarks>
internal static class RestorePhase
{
    private static readonly string Phase = BootstrapCommand.Restore.ToPhaseLogName();

    /// <summary>
    /// Compose services stopped BEFORE the scylla restore so client-side hydration
    /// queries don't race the seed shutdown. Restarted at the end of the scylla path.
    /// Kept as an internal readonly array so tests / SystemdInstallPhase can assert the
    /// canonical set without hard-coding it.
    /// </summary>
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

        // Destructive-op confirmation. Non-interactive callers MUST pass --force so a
        // rogue systemd unit or CI pipeline can't accidentally wipe a data volume.
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

    /// <summary>
    /// Applies the CLI archive selectors + <c>--restore-latest</c> to produce the pair
    /// of paths that will actually be restored. Explicit paths always win over
    /// <c>--restore-latest</c>; the resolver only fills in a component that wasn't
    /// explicitly named. Returns <c>(null, null)</c> if neither component was chosen —
    /// the caller surfaces that as a phase failure.
    /// </summary>
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

    /// <summary>
    /// Builds the <c>docker compose exec</c> argv for <c>pg_restore</c>. Uses
    /// <c>--clean --if-exists</c> so a re-run against the same live database is
    /// idempotent (pg_restore emits <c>DROP ... IF EXISTS</c> before the recreate).
    /// The <c>--single-transaction</c> flag ensures a partial failure leaves the DB
    /// in the pre-restore state instead of a half-restored soup. Internal for
    /// unit-test coverage of the argv shape.
    /// </summary>
    internal static IReadOnlyList<string> BuildPgRestoreArgs(
        string composeFile, string adminUser, string database)
        => DockerCompose.BuildPostgresExecArgs(
            composeFile, ComposeServices.Postgres, "pg_restore", adminUser, database,
            "--clean", "--if-exists",
            "--single-transaction",
            "--no-owner");

    /// <summary>
    /// Builds the top-level <c>docker cp - &lt;id&gt;:&lt;path&gt;</c> argv used to
    /// stream a host-side tar payload back into a running container. Mirror of
    /// <see cref="BackupPhase.BuildContainerCpArgs"/> — the source (<c>-</c>) means
    /// "read a tar from stdin", the destination is the container-side path to write
    /// into. Internal for unit-test coverage.
    /// </summary>
    internal static IReadOnlyList<string> BuildContainerCpWriteArgs(string containerId, string dataPath)
        => DockerCompose.BuildContainerCpIntoContainer(containerId, dataPath);

    private static async Task RestorePostgresAsync(
        string composeFile, string archivePath, BootstrapConfig config, GeneratedSecrets secrets,
        PhaseLogger logger, CancellationToken ct)
    {
        var (adminUser, adminPassword) = PhaseArtifactLoader.RequireAdminPassword(secrets, logger, Phase);

        // Make sure the postgres container is up before we try to exec into it — an
        // update-images rollback path may have left the whole stack stopped. Idempotent.
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

        // Stop clients first so a hydration query doesn't race the scylla stop and hit
        // a half-shutdown coordinator. Best-effort — a service that isn't in the compose
        // graph (e.g. cassandra-mode deployments don't ship octocon-web when web=false)
        // exits non-zero, which we downgrade to a warning.
        foreach (var client in ScyllaRestoreClientServices)
        {
            var stop = await DockerCompose.StopAsync(composeFile, [client], ct: ct).ConfigureAwait(false);
            if (stop.ExitCode != 0)
            {
                logger.Warn($"docker compose stop {client} exited {stop.ExitCode} (missing service?): {stop.StdErr.Trim()}");
            }
        }

        // Stop the seed so the file locks release before we overwrite the data volume.
        logger.Info($"    scylla: stopping {service}");
        await PhaseRunner.RunOrPhaseFailAsync(
            () => DockerCompose.StopAsync(composeFile, [service], ct: ct),
            logger, Phase, PhaseFailureReasons.StopScylla,
            $"docker compose stop {service}", ct).ConfigureAwait(false);

        // We need a live container to run `docker cp` against. Compose stop leaves the
        // container in the stopped state (docker cp is happy with that), but if the
        // container had never been created (fresh box) we bring it up first, then stop
        // it, so the cp target exists.
        var containerId = await DockerCompose.ResolveContainerIdAsync(composeFile, service, includeStopped: true, ct: ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(containerId))
        {
            // Create the container without starting it. `compose up --no-start` does exactly
            // that on all compose v2 versions we care about.
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

        // Wipe the destination path inside the container before streaming the archive
        // in. `docker cp -` extracts the tar over the top of the existing directory,
        // which would leave any pre-existing files (SSTables from the failed update
        // attempt, etc.) sitting alongside the restored ones and confuse Scylla's
        // startup. `docker exec` needs a running container, but the container is
        // stopped right now — the wipe is done via a throwaway short-lived container
        // that mounts the same data volume via `--volumes-from`. Simpler alternative:
        // use `docker cp` to also delete a marker file first — not viable, cp doesn't
        // delete. So we run a one-shot `sh -c 'rm -rf .../ *'` in a helper alpine
        // container mounted with `--volumes-from <id>`. That's the portable way to
        // mutate a stopped container's volume without knowing its host mountpoint.
        logger.Info($"    scylla: wiping {dataPath} inside container");
        var wipe = await ProcessRunner.RunAsync("docker",
            ["run", "--rm", "--volumes-from", containerId, "alpine:3.20", "sh", "-c",
             $"rm -rf {dataPath}/* {dataPath}/.[!.]* 2>/dev/null || true"],
            ct: ct).ConfigureAwait(false);
        if (wipe.ExitCode != 0)
        {
            // Non-fatal on some rootless-docker setups where the volume unmounts oddly;
            // log and continue — docker cp will overlay the archive contents in any case.
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

        // Bring the seed back. It re-hydrates against the restored SSTables during
        // its normal startup path; we don't have to reload anything explicitly.
        logger.Info($"    scylla: starting {service}");
        await PhaseRunner.RunOrPhaseFailAsync(
            () => DockerCompose.StartAsync(composeFile, [service], ct: ct),
            logger, Phase, PhaseFailureReasons.StartScylla,
            $"docker compose start {service}", ct).ConfigureAwait(false);

        // Restart clients. Order matters: API first (so the web tier's health probe
        // hits a live upstream), then web.
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
        // Restore already runs against a live, warmed-up cluster (we've just paused the
        // client tier around a hot swap), so the first successful pg_isready is a
        // sufficient handoff signal — the 3-in-a-row check DatabaseInitPhase runs would
        // pay ~4s per invocation for no meaningful safety gain here.
        return Util.PostgresReadinessProbe.WaitAsync(
            composeFile,
            ComposeServices.Postgres,
            TimeSpan.FromMinutes(10),
            logger,
            ct,
            runAsRole: Interfold.Contracts.Configuration.PostgresRoles.Init);
    }

}


