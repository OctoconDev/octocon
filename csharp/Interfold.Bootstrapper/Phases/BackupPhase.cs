using System.Globalization;
using System.IO.Compression;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// One-shot logical backup of the live Postgres + Scylla/Cassandra state. Runs after
/// <see cref="LaunchPhase"/> has the stack up; idempotent across reruns. Driven either
/// manually by the operator (<c>interfold-bootstrap backup</c>) or unattended by the
/// systemd timer installed via <see cref="SystemdInstallPhase"/>.
/// </summary>
/// <remarks>
/// <para>
/// Backup layout (per component) is <c>{backupDir}/{postgres|scylla}/{timestamp}.{ext}</c>
/// where:
/// <list type="bullet">
///   <item><c>backupDir</c> resolves to the operator-supplied path, otherwise
///         <c>{outputDir}/backups</c>.</item>
///   <item><c>postgres</c>: <c>.dump</c> custom-format pg_dump (restorable via
///         <c>pg_restore</c>). Authenticates as the <c>{user}_admin</c> role created by
///         <see cref="DatabaseInitPhase"/>; password sourced from
///         <c>secrets/secrets.json</c> via the <c>PGPASSWORD</c> env var so it never
///         appears on the docker argv.</item>
///   <item><c>scylla</c>: <c>.tar.gz</c> of <c>nodetool snapshot</c> contents from the
///         seed node only. The archive is produced host-side via <c>docker cp
///         &lt;container&gt;:/var/lib/scylla -</c> piped through a <see cref="GZipStream"/> —
///         the official <c>scylladb/scylla</c> image is distroless-ish and ships no
///         <c>tar</c>, so an in-container <c>tar</c> exits 127. <c>docker cp</c> is a
///         daemon-level operation that streams a raw tar of the source path to stdout
///         regardless of what binaries the container has. Multi-DC operators that want
///         all seven regional nodes captured do that with their own wrapper — documented
///         in <see cref="DatabaseMode"/>.</item>
/// </list>
/// </para>
/// <para>
/// Retention: after a successful write the phase walks <c>{backupDir}/{component}/</c> and
/// deletes the oldest archives (by file mtime) until exactly <c>RetainCount</c> remain.
/// The pure pruning logic lives in <see cref="BackupRetention"/> so the unit tests can
/// drive every edge case without staging real files.
/// </para>
/// </remarks>
internal static class BackupPhase
{
    private static readonly string Phase = BootstrapCommand.Backup.ToPhaseLogName();

    /// <summary>
    /// Allowed values for <see cref="BootstrapOptions.BackupComponent"/>. Kept as an array
    /// so the validator can surface the canonical set in its error message.
    /// </summary>
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

        var configPath = BootstrapArtifactPaths.ResolveConfigPath(options);
        if (!File.Exists(configPath))
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.MissingConfig);
            throw new InvalidOperationException(
                $"Backup requires a populated bootstrap config at {configPath}. " +
                "Run `bootstrap` first.");
        }

        BootstrapConfig config;
        await using (var stream = File.OpenRead(configPath))
        {
            config = await System.Text.Json.JsonSerializer.DeserializeAsync(
                stream, BootstrapJsonContext.Default.BootstrapConfig, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Failed to parse {configPath}.");
        }

        GeneratedSecrets secrets;
        try
        {
            secrets = SecretsPhase.LoadExisting(options);
        }
        catch (InvalidOperationException ex)
        {
            // Re-throw with a phase-specific message; the original carries the secrets-phase
            // wording which is misleading in a backup context.
            logger.PhaseFail(Phase, PhaseFailureReasons.MissingSecrets);
            throw new InvalidOperationException(
                $"Backup requires the admin credentials in secrets/secrets.json under {options.OutputDir}. " +
                "Run `bootstrap` first to generate them.", ex);
        }

        var composeFile = FindComposeFile(options.OutputDir);
        if (composeFile is null)
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.NoComposeFile);
            throw new InvalidOperationException(
                $"docker-compose.yaml not found under {options.OutputDir}. Run `bootstrap publish` first.");
        }
        logger.Info($"    using compose file {composeFile}");

        var backupRoot = ResolveBackupRoot(options, config);
        var retainCount = options.BackupRetainOverride ?? config.Backup.RetainCount;
        if (retainCount < 1)
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.InvalidRetain);
            throw new InvalidOperationException(
                $"--retain={retainCount} is below the minimum of 1.");
        }
        logger.Info($"    backup root: {backupRoot} (retain {retainCount} per component)");

        // Stable timestamp shared by every artifact this run produces, so a postgres+scylla
        // pair can be correlated by filename without parsing inside-the-file metadata.
        // ISO-ish, sortable, no separators that need escaping on a Unix filesystem.
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

    /// <summary>
    /// Resolves the on-disk backup root for this invocation. Precedence:
    /// <list type="number">
    ///   <item><c>--backup-dir</c> on the CLI (operator escape hatch for one-shot runs).</item>
    ///   <item><c>config.backup.directory</c> when non-empty (the systemd-timer path).</item>
    ///   <item>Default: <c>{outputDir}/backups</c>.</item>
    /// </list>
    /// Internal so the unit tests can assert the precedence rules without driving the full phase.
    /// </summary>
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

    /// <summary>
    /// Resolves the Scylla/Cassandra seed service name + container-side data path for the
    /// configured <see cref="BootstrapConfig.DatabaseMode"/>. Mirrors the resource-naming
    /// rules in <c>InterfoldAppHost.Configure</c> so the docker-compose exec lands on the
    /// same container the AppHost graph spun up. Internal for unit testing.
    /// </summary>
    internal static (string Service, string DataPath) ResolveScyllaSeed(BootstrapConfig config)
    {
        return config.DatabaseMode switch
        {
            DatabaseMode.Cassandra => (ComposeServices.Cassandra, ContainerMountPaths.CassandraData),
            DatabaseMode.Multi => (ComposeServices.ScyllaNam, ContainerMountPaths.ScyllaData),
            _ => (ComposeServices.ScyllaSingle, ContainerMountPaths.ScyllaData),
        };
    }

    /// <summary>
    /// Computes the canonical archive filename for a given component + timestamp. Returned
    /// path is relative to the component subdirectory; callers join with the backup root.
    /// </summary>
    internal static string BuildArchiveFileName(BackupDatabaseComponent component, string timestamp)
    {
        return component switch
        {
            BackupDatabaseComponent.Postgres => $"{timestamp}.dump",
            BackupDatabaseComponent.Scylla => $"{timestamp}.tar.gz",
            _ => throw new InvalidOperationException($"Unknown component '{component}' (expected: postgres | scylla)."),
        };
    }

    /// <summary>
    /// Builds the docker-compose exec argv for the Postgres backup probe. Internal so the
    /// unit-test project can assert the argv shape without invoking docker. The
    /// <c>PGPASSWORD</c> env var is set on the exec via <c>--env</c> so the admin password
    /// never appears on the process argv (which would otherwise be visible to anyone with
    /// <c>ps</c>).
    /// </summary>
    internal static IReadOnlyList<string> BuildPostgresDumpArgs(
        string composeFile, string adminUser, string database)
    {
        return
        [
            "compose", "-f", composeFile,
            "exec", "-T",
            "--env", "PGPASSWORD",
            ComposeServices.Postgres,
            "pg_dump",
            "-U", adminUser,
            "-d", database,
            "-Fc",
        ];
    }

    /// <summary>
    /// Builds the docker-compose exec argv for the Scylla/Cassandra nodetool snapshot
    /// bracket. Returns three argv lists (snapshot, resolve-container, clearsnapshot) so
    /// the caller can drive each step in sequence. The middle list resolves the seed
    /// node's container id (via <c>docker compose ps -q</c>) so the caller can then
    /// <c>docker cp</c> the snapshot contents out — the archive itself is produced
    /// host-side (see <see cref="BuildContainerCpArgs"/>) because the
    /// <c>scylladb/scylla</c> image ships no <c>tar</c> binary.
    /// Internal so unit tests can assert the argv shape without invoking docker.
    /// </summary>
    internal static (IReadOnlyList<string> Snapshot, IReadOnlyList<string> ResolveContainer, IReadOnlyList<string> Clear)
        BuildScyllaSnapshotArgs(string composeFile, string service, string dataPath, string tag)
    {
        _ = dataPath; // consumed by BuildContainerCpArgs downstream; kept in the signature so
                     // callers pass one cohesive set of parameters, and to preserve the
                     // symmetry with BuildPostgresDumpArgs.
        var snapshot = new[]
        {
            "compose", "-f", composeFile,
            "exec", "-T", service,
            "nodetool", "snapshot", "-t", tag,
        };
        // Container id is a runtime handle (docker assigns it when compose brings the
        // service up), so we can't bake it into an argv template — this step resolves it.
        var resolveContainer = new[]
        {
            "compose", "-f", composeFile,
            "ps", "-q", service,
        };
        var clear = new[]
        {
            "compose", "-f", composeFile,
            "exec", "-T", service,
            "nodetool", "clearsnapshot", "-t", tag,
        };
        return (snapshot, resolveContainer, clear);
    }

    /// <summary>
    /// Builds the top-level <c>docker cp</c> argv for streaming a container path to the
    /// host as a raw tar archive on stdout. Piping the container id (not the compose
    /// service name) because <c>docker cp</c> is a daemon-level API that only speaks
    /// container ids/names, and passing <c>-</c> as the destination emits the tar to
    /// stdout instead of writing a file on the host. Internal so unit tests can assert
    /// the argv shape without invoking docker.
    /// </summary>
    internal static IReadOnlyList<string> BuildContainerCpArgs(string containerId, string dataPath)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            throw new ArgumentException("containerId must be non-empty.", nameof(containerId));
        }
        if (string.IsNullOrWhiteSpace(dataPath))
        {
            throw new ArgumentException("dataPath must be non-empty.", nameof(dataPath));
        }
        return ["cp", $"{containerId}:{dataPath}", "-"];
    }

    private static async Task BackupPostgresAsync(
        string composeFile, string backupRoot, string timestamp,
        BootstrapConfig config, GeneratedSecrets secrets, int retainCount,
        PhaseLogger logger, CancellationToken ct)
    {
        var componentDir = Path.Combine(backupRoot, BackupStoragePaths.PostgresDir);
        Directory.CreateDirectory(componentDir);
        var dumpPath = Path.Combine(componentDir, BuildArchiveFileName(BackupDatabaseComponent.Postgres, timestamp));

        // Admin role created by DatabaseInitPhase. We don't try to run the dump as the app
        // role — pg_dump needs broader privileges to capture every object regardless of
        // ownership, and the admin role is exactly what `DatabaseInitPhase.BuildPostgresSeedOptions`
        // already provisioned for that purpose (see csharp/Interfold.Bootstrapper/Phases/DatabaseInitPhase.cs).
        var adminUser = $"{secrets.PostgresUser}_admin";
        var adminPassword = secrets.PostgresAdminPassword;
        if (string.IsNullOrEmpty(adminPassword))
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.MissingAdminPassword);
            throw new InvalidOperationException(
                "secrets/secrets.json does not contain a PostgresAdminPassword. " +
                "Either it predates DatabaseInitPhase or it was hand-edited; re-run `bootstrap`.");
        }

        logger.Info($"    postgres: pg_dump -> {dumpPath}");
        var argv = BuildPostgresDumpArgs(composeFile, adminUser, config.PostgresDatabase);
        var env = new Dictionary<string, string?> { ["PGPASSWORD"] = adminPassword };

        // Stream stdout straight to the output file so the dump never sits in memory.
        await StreamProcessStdoutToFileAsync("docker", argv, env, dumpPath, ct).ConfigureAwait(false);

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

        // Snapshot tag pinned to the timestamp so a failed clear (e.g. compose down between
        // snapshot and clear) leaves an obvious orphan an operator can match to the failed
        // run. The tag is purely a name on disk inside the container.
        var tag = $"interfold-backup-{timestamp}";

        var (snapshotArgs, resolveContainerArgs, clearArgs) =
            BuildScyllaSnapshotArgs(composeFile, service, dataPath, tag);

        logger.Info($"    scylla: nodetool snapshot -t {tag} (service={service})");
        var snapshot = await ProcessRunner.RunAsync("docker", snapshotArgs, ct: ct).ConfigureAwait(false);
        if (snapshot.ExitCode != 0)
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.NodetoolSnapshot);
            throw new InvalidOperationException(
                $"nodetool snapshot exited {snapshot.ExitCode} on {service}: {snapshot.StdErr.Trim()}");
        }

        try
        {
            // docker cp is a daemon-level API that only accepts container ids / names, so
            // resolve the seed's runtime id first. `docker compose ps -q <service>` prints
            // one id per line; take the first (there is only ever one for the seed
            // service in the compose graph the AppHost emits).
            var resolve = await ProcessRunner.RunAsync("docker", resolveContainerArgs, ct: ct).ConfigureAwait(false);
            if (resolve.ExitCode != 0 || string.IsNullOrWhiteSpace(resolve.StdOut))
            {
                logger.PhaseFail(Phase, PhaseFailureReasons.ResolveScyllaContainer);
                throw new InvalidOperationException(
                    $"Failed to resolve container id for compose service '{service}'. " +
                    $"Exit={resolve.ExitCode}, stderr='{resolve.StdErr.Trim()}'. " +
                    "Is the stack running? Try `docker compose ps` under the output directory.");
            }
            var containerId = resolve.StdOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .First();

            logger.Info($"    scylla: docker cp {service}({containerId[..Math.Min(12, containerId.Length)]}):{dataPath} -> {archivePath}");

            // docker cp <id>:<path> - writes a raw tar of <path>'s contents to stdout.
            // We wrap the destination stream in a GZipStream on the host so what lands on
            // disk is a real .tar.gz that pairs with the .tar.gz filename convention.
            // This deliberately does NOT shell out to gzip — keeping the pipe entirely
            // in-process avoids the sh/pipefail semantics headache and works identically
            // on any host with just docker installed.
            var cpArgs = BuildContainerCpArgs(containerId, dataPath);
            await StreamProcessStdoutToFileAsync(
                "docker", cpArgs, environment: null, archivePath, ct, compress: true)
                .ConfigureAwait(false);
        }
        finally
        {
            // Best-effort clearsnapshot regardless of cp success/failure — leftover
            // snapshots eat disk on the scylla container indefinitely otherwise. Failure
            // here is logged but doesn't fail the phase (the cp already succeeded or
            // already failed; the dispositive verdict is the operator-visible archive).
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

    private static void PruneComponent(string componentDir, BackupDatabaseComponent component, int retainCount, PhaseLogger logger)
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

    private static string? FindComposeFile(string outputDir) => BootstrapArtifactPaths.FindComposeFile(outputDir);

    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="arguments"/> and streams its
    /// stdout directly into <paramref name="destinationPath"/>. Throws if the process exits
    /// non-zero; partial output is preserved on disk for diagnosis but the caller deletes
    /// it before propagating the exception when it's known to be empty/corrupt.
    /// </summary>
    /// <param name="compress">
    /// When true, the destination file is wrapped in a <see cref="GZipStream"/> so the
    /// process's stdout is written as gzip-compressed bytes on disk. Used by the Scylla
    /// path where <c>docker cp</c> emits an uncompressed tar but the target filename is
    /// <c>.tar.gz</c>. Postgres path leaves this false — <c>pg_dump -Fc</c> already emits
    /// a compressed custom-format archive.
    /// </param>
    private static async Task StreamProcessStdoutToFileAsync(
        string fileName, IReadOnlyList<string> arguments,
        IDictionary<string, string?>? environment, string destinationPath,
        CancellationToken ct, bool compress = false)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        if (environment is not null)
        {
            foreach (var kvp in environment)
            {
                psi.Environment[kvp.Key] = kvp.Value;
            }
        }

        using var proc = new System.Diagnostics.Process { StartInfo = psi };
        var stderr = new System.Text.StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        if (!proc.Start())
        {
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");
        }
        proc.BeginErrorReadLine();

        try
        {
            await using (var fs = File.Create(destinationPath))
            {
                if (compress)
                {
                    // CompressionLevel.Fastest keeps CPU well below the docker-cp / disk
                    // throughput ceiling on typical backup data (SSTables compress poorly
                    // anyway because they're already LZ4-compressed internally). Higher
                    // levels would burn CPU for a marginal size win on already-compressed
                    // payload.
                    await using var gz = new GZipStream(fs, CompressionLevel.Fastest, leaveOpen: false);
                    await proc.StandardOutput.BaseStream.CopyToAsync(gz, ct).ConfigureAwait(false);
                }
                else
                {
                    await proc.StandardOutput.BaseStream.CopyToAsync(fs, ct).ConfigureAwait(false);
                }
            }
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{fileName} {string.Join(' ', arguments)} exited {proc.ExitCode}: {stderr.ToString().Trim()}");
        }
    }

    private static string FormatBytes(long bytes)
    {
        // KiB/MiB/GiB — operator-facing diagnostic only, no need to nail down the unit edge
        // cases or culture-specific formatting beyond invariant.
        const long Kib = 1024L;
        const long Mib = Kib * 1024L;
        const long Gib = Mib * 1024L;
        if (bytes >= Gib) return $"{bytes / (double)Gib:F2} GiB";
        if (bytes >= Mib) return $"{bytes / (double)Mib:F2} MiB";
        if (bytes >= Kib) return $"{bytes / (double)Kib:F2} KiB";
        return $"{bytes} B";
    }
}

/// <summary>
/// Pure pruning helper: given an unordered set of backup files and a target retention
/// count, returns the files to delete (oldest by last-write time). Extracted out of
/// <see cref="BackupPhase"/> so the retention semantics can be exhaustively unit-tested
/// without staging real files on disk.
/// </summary>
internal static class BackupRetention
{
    /// <summary>
    /// Selects the files to delete so the surviving set has exactly <paramref name="keep"/>
    /// entries. When the input has &lt;= <paramref name="keep"/> files, returns an empty
    /// sequence. Ordering of the returned files is oldest-first.
    /// </summary>
    /// <param name="files">Candidate backup archives in the component subdirectory.</param>
    /// <param name="keep">Target survivor count. Must be &gt;= 0; zero deletes everything.</param>
    public static IEnumerable<FileInfo> Prune(IEnumerable<FileInfo> files, int keep)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (keep < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(keep), keep, "retain count must be >= 0.");
        }

        // Materialise once — both the count and the order matter, and the caller hands us a
        // freshly-enumerated DirectoryInfo result that doesn't survive a second pass anyway.
        var ordered = files.OrderBy(f => f.LastWriteTimeUtc).ToList();
        var toDeleteCount = ordered.Count - keep;
        return toDeleteCount <= 0
            ? []
            : ordered.Take(toDeleteCount);
    }
}
