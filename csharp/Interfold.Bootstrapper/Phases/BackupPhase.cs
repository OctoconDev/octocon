using System.Globalization;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;

namespace Interfold.Bootstrapper.Phases;

/// <summary>Logical backup of live Postgres + Scylla/Cassandra state; idempotent across
/// reruns. Driven manually (<c>interfold-bootstrap backup</c>) or by the systemd timer from
/// <see cref="SystemdInstallPhase"/>. Layout: <c>{backupDir}/{postgres|scylla}/{timestamp}.{ext}</c>.
/// Postgres → custom-format <c>pg_dump</c> as the <c>{user}_admin</c> role, PGPASSWORD via
/// env (never argv). Scylla → nodetool snapshot + host-side <c>docker cp</c> piped through
/// <see cref="System.IO.Compression.GZipStream"/> (the scylladb/scylla image ships no <c>tar</c>);
/// seed node only, multi-DC wrappers layer over this. Post-write prune keeps exactly
/// <c>RetainCount</c> archives per component.</summary>
internal static class BackupPhase
{
    private static readonly string Phase = BootstrapCommand.Backup.ToPhaseLogName();

    internal static readonly string[] ValidComponents =
        Enum.GetValues<BackupDatabaseComponent>().Select(BackupDatabaseComponentExtensions.ToWireValue).ToArray();

    public static async Task<int> RunAsync(BootstrapOptions options, PhaseLogger logger, CancellationToken ct)
    {
        logger.PhaseStart(Phase);

        if (BackupDatabaseComponentExtensions.TryParse(options.BackupComponent) is not { } component)
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.UnknownComponent);
            throw new InvalidOperationException(
                $"--component='{options.BackupComponent}' is invalid. Expected one of: {string.Join(", ", ValidComponents)}.");
        }

        var config = await PhaseArtifactLoader
            .LoadRequiredConfigAsync(options, logger, Phase, "Backup", ct)
            .ConfigureAwait(false);

        var secrets = PhaseArtifactLoader.LoadRequiredSecretsOrFail(options, logger, Phase, "Backup");

        var composeFile = PhaseArtifactLoader.RequireComposeFileOrFail(options, logger, Phase);

        var backupRoot = ResolveBackupRoot(options, config);
        var retainCount = options.BackupRetainOverride ?? config.Backup.RetainCount;
        if (retainCount < 1)
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.InvalidRetain);
            throw new InvalidOperationException(
                $"--retain={retainCount} is below the minimum of 1.");
        }
        logger.Info($"    backup root: {backupRoot} (retain {retainCount} per component)");

        // Shared timestamp so a postgres+scylla pair correlates by filename alone.
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        if (component is BackupDatabaseComponent.Postgres or BackupDatabaseComponent.All)
        {
            await BackupPostgresAsync(composeFile, backupRoot, timestamp, config, secrets, retainCount, logger, ct)
                .ConfigureAwait(false);
        }

        if (component is BackupDatabaseComponent.Scylla or BackupDatabaseComponent.All)
        {
            await BackupScyllaAsync(composeFile, backupRoot, timestamp, config, retainCount, logger, ct)
                .ConfigureAwait(false);
        }

        logger.PhaseDone(Phase);
        return 0;
    }

    /// <summary>Precedence: <c>--backup-dir</c> CLI → <c>config.backup.directory</c> →
    /// <c>{outputDir}/backups</c>.</summary>
    internal static string ResolveBackupRoot(BootstrapOptions options, BootstrapConfig config)
    {
        if (!string.IsNullOrWhiteSpace(options.BackupDirOverride))
        {
            return Path.GetFullPath(options.BackupDirOverride);
        }
        if (!string.IsNullOrWhiteSpace(config.Backup.Directory))
        {
            return Path.GetFullPath(config.Backup.Directory);
        }
        return Path.Combine(options.OutputDir, "backups");
    }

    /// <summary>Scylla/Cassandra seed service + container data path. Mirrors the resource
    /// naming in <c>InterfoldAppHost.Configure</c> so exec lands on the same container.</summary>
    internal static (string Service, string DataPath) ResolveScyllaSeed(BootstrapConfig config)
    {
        return config.DatabaseMode switch
        {
            DatabaseMode.Cassandra => (ComposeServices.Cassandra, ContainerMountPaths.CassandraData),
            DatabaseMode.Multi => (ComposeServices.ScyllaNam, ContainerMountPaths.ScyllaData),
            _ => (ComposeServices.ScyllaSingle, ContainerMountPaths.ScyllaData),
        };
    }

    /// <summary>Canonical archive filename (relative to the component subdirectory).</summary>
    internal static string BuildArchiveFileName(BackupDatabaseComponent component, string timestamp)
    {
        return component switch
        {
            BackupDatabaseComponent.Postgres => $"{timestamp}.dump",
            BackupDatabaseComponent.Scylla => $"{timestamp}.tar.gz",
            _ => throw new InvalidOperationException($"Unknown component '{component}' (expected: postgres | scylla)."),
        };
    }

    /// <summary>pg_dump docker-compose exec argv. PGPASSWORD flows via env, not argv,
    /// so it stays invisible to <c>ps</c>.</summary>
    internal static IReadOnlyList<string> BuildPostgresDumpArgs(
        string composeFile, string adminUser, string database)
        => DockerCompose.BuildPostgresExecArgs(
            composeFile, ComposeServices.Postgres, "pg_dump", adminUser, database,
            "-Fc");

    /// <summary>Returns <c>(snapshot, clearsnapshot)</c> argv pair. The intervening
    /// <c>docker cp</c> (see <see cref="BuildContainerCpArgs"/>) produces the archive
    /// host-side because scylladb/scylla ships no <c>tar</c>.</summary>
    internal static (IReadOnlyList<string> Snapshot, IReadOnlyList<string> Clear)
        BuildScyllaSnapshotArgs(string composeFile, string service, string dataPath, string tag)
    {
        _ = dataPath; // Consumed downstream by BuildContainerCpArgs; kept for signature symmetry.
        var snapshot = new[]
        {
            "compose", "-f", composeFile,
            "exec", "-T", service,
            "nodetool", "snapshot", "-t", tag,
        };
        var clear = new[]
        {
            "compose", "-f", composeFile,
            "exec", "-T", service,
            "nodetool", "clearsnapshot", "-t", tag,
        };
        return (snapshot, clear);
    }

    /// <summary><c>docker cp</c> argv streaming the container path as a raw tar on stdout.
    /// Container id (not service name) because <c>docker cp</c> is a daemon-level API;
    /// <c>-</c> destination emits to stdout instead of a host file.</summary>
    internal static IReadOnlyList<string> BuildContainerCpArgs(string containerId, string dataPath)
        => DockerCompose.BuildContainerCpFromContainer(containerId, dataPath);

    private static async Task BackupPostgresAsync(
        string composeFile, string backupRoot, string timestamp,
        BootstrapConfig config, GeneratedSecrets secrets, int retainCount,
        PhaseLogger logger, CancellationToken ct)
    {
        var componentDir = Path.Combine(backupRoot, BackupStoragePaths.PostgresDir);
        Directory.CreateDirectory(componentDir);
        var dumpPath = Path.Combine(componentDir, BuildArchiveFileName(BackupDatabaseComponent.Postgres, timestamp));

        // pg_dump needs the admin role — the app role's per-object grants aren't broad enough
        // to capture ownership metadata. Provisioned by DatabaseInitPhase.
        var (adminUser, adminPassword) = PhaseArtifactLoader.RequireAdminPassword(secrets, logger, Phase);

        logger.Info($"    postgres: pg_dump -> {dumpPath}");
        var argv = BuildPostgresDumpArgs(composeFile, adminUser, config.PostgresDatabase);

        // Stream to disk so the dump never buffers in memory; password via env, never argv.
        await DatabaseArchiveStreamer.StreamProcessStdoutToFileAsync(
            "docker", argv, DatabaseArchiveStreamer.PgPasswordEnv(adminPassword), dumpPath, ct)
            .ConfigureAwait(false);

        var size = new FileInfo(dumpPath).Length;
        if (size == 0)
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.EmptyPostgresDump);
            File.Delete(dumpPath);
            throw new InvalidOperationException(
                $"pg_dump produced an empty file at {dumpPath}. Inspect docker logs for {ComposeServices.Postgres}.");
        }
        logger.Info($"    postgres: wrote {FormatBytes(size)}");

        PruneComponent(componentDir, BackupDatabaseComponent.Postgres, retainCount, logger);
    }

    private static async Task BackupScyllaAsync(
        string composeFile, string backupRoot, string timestamp,
        BootstrapConfig config, int retainCount,
        PhaseLogger logger, CancellationToken ct)
    {
        var (service, dataPath) = ResolveScyllaSeed(config);
        var componentDir = Path.Combine(backupRoot, BackupStoragePaths.ScyllaDir);
        Directory.CreateDirectory(componentDir);
        var archivePath = Path.Combine(componentDir, BuildArchiveFileName(BackupDatabaseComponent.Scylla, timestamp));

        // Tag pinned to the timestamp so a failed clear leaves an obvious orphan matching this run.
        var tag = $"interfold-backup-{timestamp}";

        var (snapshotArgs, clearArgs) =
            BuildScyllaSnapshotArgs(composeFile, service, dataPath, tag);

        logger.Info($"    scylla: nodetool snapshot -t {tag} (service={service})");
        await PhaseRunner.RunOrPhaseFailAsync(
            () => ProcessRunner.RunAsync("docker", snapshotArgs, ct: ct),
            logger, Phase, PhaseFailureReasons.NodetoolSnapshot,
            $"nodetool snapshot", ct).ConfigureAwait(false);

        try
        {
            // docker cp needs a container id; the AppHost emits exactly one seed container.
            var containerId = await DockerCompose.ResolveContainerIdAsync(composeFile, service, ct: ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(containerId))
            {
                logger.PhaseFail(Phase, PhaseFailureReasons.ResolveScyllaContainer);
                throw new InvalidOperationException(
                    $"Failed to resolve container id for compose service '{service}'. " +
                    "Is the stack running? Try `docker compose ps` under the output directory.");
            }

            logger.Info($"    scylla: docker cp {service}({containerId[..Math.Min(12, containerId.Length)]}):{dataPath} -> {archivePath}");

            // In-process GZip wrap avoids sh/pipefail semantics and needs only docker on the host.
            var cpArgs = BuildContainerCpArgs(containerId, dataPath);
            await DatabaseArchiveStreamer.StreamProcessStdoutToFileAsync(
                "docker", cpArgs, environment: null, archivePath, ct, compress: true)
                .ConfigureAwait(false);
        }
        finally
        {
            // Best-effort clearsnapshot: leftover snapshots eat container disk otherwise.
            // Failure is logged, not fatal — the archive on disk is the dispositive verdict.
            var clear = await ProcessRunner.RunAsync("docker", clearArgs, ct: ct).ConfigureAwait(false);
            if (clear.ExitCode != 0)
            {
                logger.Warn($"nodetool clearsnapshot -t {tag} exited {clear.ExitCode}: {clear.StdErr.Trim()}");
            }
        }

        var size = new FileInfo(archivePath).Length;
        if (size == 0)
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.EmptyScyllaArchive);
            File.Delete(archivePath);
            throw new InvalidOperationException(
                $"Scylla tar produced an empty file at {archivePath}. Inspect docker logs for {service}.");
        }
        logger.Info($"    scylla: wrote {FormatBytes(size)}");

        PruneComponent(componentDir, BackupDatabaseComponent.Scylla, retainCount, logger);
    }

    internal static void PruneComponent(string componentDir, BackupDatabaseComponent component, int retainCount, PhaseLogger logger)
    {
        var pattern = component switch
        {
            BackupDatabaseComponent.Postgres => BackupStoragePaths.PostgresArchivePattern,
            BackupDatabaseComponent.Scylla => BackupStoragePaths.ScyllaArchivePattern,
            _ => throw new InvalidOperationException($"Unknown component '{component}'."),
        };
        var files = new DirectoryInfo(componentDir)
            .EnumerateFiles(pattern, SearchOption.TopDirectoryOnly)
            .ToList();

        foreach (var stale in BackupRetention.Prune(files, retainCount))
        {
            try
            {
                stale.Delete();
                logger.Info($"    {component}: pruned {stale.Name}");
            }
            catch (Exception ex)
            {
                logger.Warn($"failed to delete {stale.FullName}: {ex.Message}");
            }
        }
    }

    private static string FormatBytes(long bytes)
    {
        const long Kib = 1024L;
        const long Mib = Kib * 1024L;
        const long Gib = Mib * 1024L;
        if (bytes >= Gib) return $"{bytes / (double)Gib:F2} GiB";
        if (bytes >= Mib) return $"{bytes / (double)Mib:F2} MiB";
        if (bytes >= Kib) return $"{bytes / (double)Kib:F2} KiB";
        return $"{bytes} B";
    }
}

/// <summary>Pure pruning helper — returns files to delete (oldest by last-write time) so the
/// survivor set is exactly <paramref name="keep"/> entries. Extracted from
/// <see cref="BackupPhase"/> for exhaustive unit testing without on-disk fixtures.</summary>
internal static class BackupRetention
{
    /// <summary>Returns files to delete, oldest-first. Empty when input ≤ keep;
    /// keep == 0 deletes everything.</summary>
    public static IEnumerable<FileInfo> Prune(IEnumerable<FileInfo> files, int keep)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (keep < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(keep), keep, "retain count must be >= 0.");
        }

        // Materialise once — DirectoryInfo enumerations don't survive a second pass.
        var ordered = files.OrderBy(f => f.LastWriteTimeUtc).ToList();
        var toDeleteCount = ordered.Count - keep;
        return toDeleteCount <= 0
            ? []
            : ordered.Take(toDeleteCount);
    }
}

