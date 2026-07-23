using System.Net;
using System.Text.Json;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;
using Interfold.Shared.Contracts.Configuration;

namespace Interfold.Bootstrapper.Phases;

/// <summary>Docker image update workflow: backup → pull → digest-diff → recreate →
/// health-check → prune. Auto-restores or prints the manual rollback recipe on failure.
/// Pre-update backup is always taken unless <see cref="BootstrapOptions.SkipPreUpdateBackup"/>
/// is set (major schema bumps need the snapshot to exist). Idempotent on "no digest change";
/// old backups are still pruned so scheduled runs can't accumulate archives forever.</summary>
internal static class UpdateImagesPhase
{
    private static readonly string Phase = BootstrapCommand.UpdateImages.ToPhaseLogName();

    public static async Task<int> RunAsync(BootstrapOptions options, PhaseLogger logger, CancellationToken ct)
    {
        logger.PhaseStart(Phase);

        var config = await PhaseArtifactLoader
            .LoadRequiredConfigAsync(options, logger, Phase, "update-images", ct)
            .ConfigureAwait(false);

        var composeFile = PhaseArtifactLoader.RequireComposeFileOrFail(options, logger, Phase);

        // CLI --service > config.update.services > every service. Empty array → pass no names
        // to `docker compose`, which interprets that as "act on every service".
        var services = ResolveServiceWhitelist(options, config);
        if (services.Count > 0)
        {
            logger.Info($"    scoping to services: {string.Join(", ", services)}");
        }
        else
        {
            logger.Info("    scoping to: every compose service");
        }

        var healthTimeout = TimeSpan.FromSeconds(
            options.HealthCheckTimeoutOverride ?? config.Update.HealthCheckTimeoutSeconds);
        var autoRestore = options.AutoRestore || config.Update.AutoRestoreOnFailure;
        var recreate = config.Update.RecreateOnUpdate;

        // Snapshot digests first so the post-pull diff has a stable baseline; fail fast if
        // the stack isn't up rather than silently always-recreate.
        logger.Info("    snapshotting pre-pull image digests");
        var preDigests = await SnapshotImageDigestsAsync(composeFile, logger, ct).ConfigureAwait(false);

        // `all` is the only sensible choice — a partial snapshot couldn't feed an auto-restore.
        (string PostgresArchive, string ScyllaArchive)? backupArtifacts = null;
        if (!options.SkipPreUpdateBackup)
        {
            logger.Info("    pre-update backup: forcing component=all");
            var backupOptions = options with
            {
                Command = BootstrapCommand.Backup,
                BackupComponent = BackupDatabaseComponent.All.ToWireValue(),
            };
            var backupExit = await BackupPhase.RunAsync(backupOptions, logger, ct).ConfigureAwait(false);
            if (backupExit != 0)
            {
                logger.PhaseFail(Phase, PhaseFailureReasons.PreUpdateBackupFailed);
                return backupExit;
            }
            backupArtifacts = ResolveLatestBackupArtifacts(backupOptions, config);
            if (backupArtifacts is { } bs)
            {
                logger.Info($"    backup complete: postgres={Path.GetFileName(bs.PostgresArchive)} scylla={Path.GetFileName(bs.ScyllaArchive)}");
            }
        }
        else
        {
            logger.Warn("--skip-pre-update-backup set; NO pre-update backup will be taken");
        }

        // Rebuild interfold-cassandra:local pre-pull so Dockerfile edits land via update-images
        // without a `bootstrap publish` re-run. Docker's layer cache makes this cheap on no-op.
        // Pull side is handled by PublishPhase.StampCassandraPullPolicyNever.
        if (ShouldRebuildCassandra(config, services))
        {
            logger.Info("    cassandra mode: rebuilding interfold-cassandra:local before pull");
            await CassandraImagePhase.EnsureBuiltAsync(logger, ct).ConfigureAwait(false);
        }

        // Non-zero here means we never left the pre-pull state; safe to leave the stack alone.
        logger.Info("    docker compose pull ...");
        var pull = await Util.DockerCompose.PullAsync(composeFile, services, ct: ct).ConfigureAwait(false);
        if (pull.ExitCode != 0)
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.PullFailed);
            throw new InvalidOperationException($"docker compose pull exited {pull.ExitCode}: {pull.StdErr.Trim()}");
        }
        // Forward both streams — docker compose writes per-service progress ("Pulling",
        // "Skipped", "Pulled") to stderr; integration tests assert on "Skipped" to prove
        // pull_policy: never fired for interfold-cassandra:local.
        if (!string.IsNullOrWhiteSpace(pull.StdOut)) logger.Info(pull.StdOut.Trim());
        if (!string.IsNullOrWhiteSpace(pull.StdErr)) logger.Info(pull.StdErr.Trim());

        // Skip recreate + health check + downtime when nothing actually changed.
        logger.Info("    snapshotting post-pull image digests");
        var postDigests = await SnapshotImageDigestsAsync(composeFile, logger, ct).ConfigureAwait(false);
        var changedServices = DiffDigests(preDigests, postDigests);
        if (changedServices.Count == 0)
        {
            logger.Info("    no-op: every image already up to date; skipping recreate");
            logger.PhaseDone(Phase);
            return 0;
        }
        logger.Info($"    {changedServices.Count} service(s) will be recreated: {string.Join(", ", changedServices)}");

        // `up -d` recreates only containers whose image ID changed, matching our diff.
        if (recreate)
        {
            await Util.DockerCompose.UpCheckedAsync(
                composeFile, services, logger, ct,
                phase: Phase, phaseFailReason: PhaseFailureReasons.UpFailed).ConfigureAwait(false);
        }
        else
        {
            logger.Warn("config.update.recreateOnUpdate=false; skipping `up -d`. " +
                        "The new image will NOT take effect until a manual `up -d` is issued.");
        }

        // Failure never leaves the stack half-updated without operator-facing recovery.
        var healthErr = await CheckStackHealthAsync(composeFile, config, healthTimeout, logger, ct).ConfigureAwait(false);
        if (healthErr is not null)
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.HealthCheckFailed);
            await OnHealthCheckFailedAsync(
                options, config, composeFile, healthErr, backupArtifacts, autoRestore, logger, ct)
                .ConfigureAwait(false);
            return 1;
        }

        // Skip pruning on --skip-pre-update-backup: no new archive justifies deleting old ones.
        if (backupArtifacts is not null)
        {
            var backupRoot = BackupPhase.ResolveBackupRoot(options, config);
            var retainCount = options.BackupRetainOverride ?? config.Backup.RetainCount;
            BackupPhase.PruneComponent(
                Path.Combine(backupRoot, BackupStoragePaths.PostgresDir),
                BackupDatabaseComponent.Postgres, retainCount, logger);
            BackupPhase.PruneComponent(
                Path.Combine(backupRoot, BackupStoragePaths.ScyllaDir),
                BackupDatabaseComponent.Scylla, retainCount, logger);
        }

        logger.PhaseDone(Phase);
        return 0;
    }

    /// <summary>CLI <c>--service</c> beats <see cref="UpdateSection.Services"/>; both empty →
    /// every service. CLI values bypass <c>ConfigPhase</c>, so they're re-checked here against
    /// <see cref="ComposeServices.AllValidUpdateServices"/>.</summary>
    internal static IReadOnlyList<string> ResolveServiceWhitelist(BootstrapOptions options, BootstrapConfig config)
    {
        if (options.UpdateServices is { Length: > 0 })
        {
            var unknown = options.UpdateServices
                .Where(svc => !ComposeServices.AllValidUpdateServices.Contains(svc, StringComparer.Ordinal))
                .ToArray();
            if (unknown.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Unknown --service value(s): {string.Join(", ", unknown)}. " +
                    $"Expected one of: {string.Join(", ", ComposeServices.AllValidUpdateServices)}.");
            }

            return options.UpdateServices;
        }
        return config.Update.Services;
    }

    /// <summary>Only cassandra mode, and only when <c>cassandra</c> is in scope; otherwise
    /// <c>--service msg-db</c> would trigger an unrelated rebuild. Pull side is handled by
    /// <see cref="PublishPhase.StampCassandraPullPolicyNever"/>.</summary>
    internal static bool ShouldRebuildCassandra(BootstrapConfig config, IReadOnlyList<string> effectiveServices)
    {
        if (!CassandraImagePhase.IsCassandraDeployment(config)) return false;
        if (effectiveServices.Count == 0) return true;
        return effectiveServices.Contains(ComposeServices.Cassandra, StringComparer.Ordinal);
    }

    /// <summary>Argv for <c>docker compose images --format json</c>.</summary>
    internal static IReadOnlyList<string> BuildComposeImagesArgs(string composeFile)
    {
        return ["compose", "-f", composeFile, "images", "--format", "json"];
    }

    /// <summary>Parses <c>docker compose images --format json</c> into service → image ID.
    /// Accepts both the JSON-array and JSON-Lines shapes (compose plugin version dependent).
    /// Duplicate service rows collapse to last-seen; replicas always recreate in lockstep.</summary>
    internal static IDictionary<string, string> ParseComposeImagesJson(string json)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return result;

        // Older compose plugin versions emit a single JSON array.
        var trimmed = json.TrimStart();
        if (trimmed.StartsWith('['))
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in doc.RootElement.EnumerateArray())
                {
                    TryAdd(entry, result);
                }
                return result;
            }
        }

        // JSON Lines (compose plugin v2.20+): one object per line.
        foreach (var line in json.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                TryAdd(doc.RootElement, result);
            }
            catch (JsonException)
            {
                // Older compose versions occasionally leak stderr into stdout; don't fail the diff.
            }
        }
        return result;

        static void TryAdd(JsonElement obj, Dictionary<string, string> into)
        {
            if (obj.ValueKind != JsonValueKind.Object) return;
            if (!obj.TryGetProperty("Service", out var svc) || svc.ValueKind != JsonValueKind.String) return;
            var service = svc.GetString();
            if (string.IsNullOrEmpty(service)) return;
            // Use the stable ID — the tag can stay the same across a floating-tag pull.
            var id = obj.TryGetProperty("ID", out var idProp) && idProp.ValueKind == JsonValueKind.String
                ? idProp.GetString() ?? string.Empty
                : string.Empty;
            into[service] = id;
        }
    }

    /// <summary>Services whose image ID changed, appeared, or disappeared. Alphabetical output.</summary>
    internal static IReadOnlyList<string> DiffDigests(
        IDictionary<string, string> before, IDictionary<string, string> after)
    {
        var changed = new List<string>();
        foreach (var kvp in after)
        {
            if (!before.TryGetValue(kvp.Key, out var oldId) || !string.Equals(oldId, kvp.Value, StringComparison.Ordinal))
            {
                changed.Add(kvp.Key);
            }
        }
        foreach (var key in before.Keys)
        {
            if (!after.ContainsKey(key)) changed.Add(key);
        }
        changed.Sort(StringComparer.Ordinal);
        return changed;
    }

    private static async Task<IDictionary<string, string>> SnapshotImageDigestsAsync(
        string composeFile, PhaseLogger logger, CancellationToken ct)
    {
        var args = BuildComposeImagesArgs(composeFile);
        var run = await ProcessRunner.RunAsync("docker", args, ct: ct).ConfigureAwait(false);
        if (run.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"docker compose images exited {run.ExitCode}: {run.StdErr.Trim()}. " +
                "Is the stack up? Try `docker compose ps` under the output directory.");
        }
        var parsed = ParseComposeImagesJson(run.StdOut);
        logger.Info($"    resolved digests for {parsed.Count} service(s)");
        return parsed;
    }

    /// <summary>Bounded probe across Postgres (pg_isready), Scylla/Cassandra (nodetool status),
    /// and the API (<c>GET /health/ready</c>). Returns null on success, else an operator-facing
    /// message. Shared deadline so no single tier can starve the others.</summary>
    private static async Task<string?> CheckStackHealthAsync(
        string composeFile, BootstrapConfig config, TimeSpan totalTimeout,
        PhaseLogger logger, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + totalTimeout;
        logger.Info($"    health-check: bounded to {totalTimeout.TotalSeconds:F0}s across postgres+scylla+api");

        // pg_isready TCP probe — succeeds only after the listener (not just the init Unix socket) is up.
        var pgErr = await WaitForPostgresReadyAsync(composeFile, deadline, logger, ct).ConfigureAwait(false);
        if (pgErr is not null) return pgErr;

        // nodetool status is the leanest probe that doesn't need the app password; passes
        // once the node reports UN.
        var (scyllaService, _) = BackupPhase.ResolveScyllaSeed(config);
        var scErr = await WaitForScyllaReadyAsync(composeFile, scyllaService, deadline, logger, ct).ConfigureAwait(false);
        if (scErr is not null) return scErr;

        var apiErr = await WaitForApiReadyAsync(config.Ports.ApiHttp, deadline, logger, ct).ConfigureAwait(false);
        if (apiErr is not null) return apiErr;

        logger.Info("    health-check: all three tiers ready");
        return null;
    }

    private static Task<string?> WaitForPostgresReadyAsync(
        string composeFile, DateTime deadline, PhaseLogger logger, CancellationToken ct)
        => PostgresReadinessProbe.TryWaitUntilAsync(composeFile, ComposeServices.Postgres, deadline, logger, ct);

    private static async Task<string?> WaitForScyllaReadyAsync(
        string composeFile, string service, DateTime deadline, PhaseLogger logger, CancellationToken ct)
    {
        var attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            // nodetool exits 0 once gossip settles and the node is UN.
            var probe = await ProcessRunner.RunAsync("docker",
                ["compose", "-f", composeFile, "exec", "-T", service, "nodetool", "status"],
                ct: ct).ConfigureAwait(false);
            if (probe.ExitCode == 0 && probe.StdOut.Contains("UN", StringComparison.Ordinal))
            {
                logger.Info($"    scylla ({service}) ready after {attempt} probe(s)");
                return null;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }
        return $"scylla ({service}) did not report UN within the health-check budget";
    }

    private static Task<string?> WaitForApiReadyAsync(
        int apiHttpPort, DateTime deadline, PhaseLogger logger, CancellationToken ct)
        => ApiReadinessProbe.TryWaitUntilAsync(apiHttpPort, deadline, logger, ct);

    private static async Task OnHealthCheckFailedAsync(
        BootstrapOptions options, BootstrapConfig config, string composeFile,
        string healthErr, (string PostgresArchive, string ScyllaArchive)? backupArtifacts,
        bool autoRestore, PhaseLogger logger, CancellationToken ct)
    {
        logger.Error($"health check failed: {healthErr}");

        // Best-effort log dump so operators don't have to shell in for a diagnosis.
        var suspects = new[] { ComposeServices.Postgres, ComposeServices.ScyllaSingle, ComposeServices.ScyllaNam, ComposeServices.Cassandra, ComposeServices.InterfoldApi, ComposeServices.OctoconWeb };
        await ComposeLogDumper.DumpAsync(composeFile, suspects, tailLines: 200, logger, ct).ConfigureAwait(false);

        // Clean stopped state so a restore/retry doesn't fight half-recreated containers.
        logger.Info("    docker compose down ...");
        var down = await DockerCompose.DownAsync(composeFile, ct: ct).ConfigureAwait(false);
        if (down.ExitCode != 0)
        {
            logger.Warn($"docker compose down exited {down.ExitCode}: {down.StdErr.Trim()}");
        }

        if (autoRestore)
        {
            if (backupArtifacts is not { } archives)
            {
                logger.Error("--auto-restore was requested but no pre-update backup exists (--skip-pre-update-backup used?).");
                return;
            }
            logger.Info("    --auto-restore: invoking RestorePhase against pre-update archives");
            var restoreOptions = options with
            {
                Command = BootstrapCommand.Restore,
                RestorePostgresArchive = archives.PostgresArchive,
                RestoreScyllaArchive = archives.ScyllaArchive,
                RestoreForce = true,
            };
            var restoreExit = await RestorePhase.RunAsync(restoreOptions, logger, ct).ConfigureAwait(false);
            if (restoreExit != 0)
            {
                logger.Error($"auto-restore failed with exit code {restoreExit}. Manual intervention required.");
            }
            return;
        }

        // Copy-pasteable manual rollback command.
        if (backupArtifacts is { } bs)
        {
            logger.Warn("update failed; the pre-update backup is on disk. To roll back manually:");
            var cmd = $"interfold-bootstrap restore --config \"{Path.GetFullPath(BootstrapArtifactPaths.ResolveConfigPath(options))}\" --output-dir \"{options.OutputDir}\" --restore-postgres \"{bs.PostgresArchive}\" --restore-scylla \"{bs.ScyllaArchive}\" --force";
            Console.Error.WriteLine();
            Console.Error.WriteLine(cmd);
            Console.Error.WriteLine();
        }
        else
        {
            logger.Warn("update failed and --skip-pre-update-backup was used; no automatic recovery path available.");
        }
    }

    /// <summary>Newest postgres + scylla archive by mtime — called immediately after the
    /// pre-update backup, so "newest" is this run's output. Null when either is missing
    /// (auto-restore downgrades to a warning).</summary>
    internal static (string PostgresArchive, string ScyllaArchive)? ResolveLatestBackupArtifacts(
        BootstrapOptions options, BootstrapConfig config)
    {
        var backupRoot = BackupPhase.ResolveBackupRoot(options, config);
        var pg = BackupStoragePaths.LatestFile(Path.Combine(backupRoot, BackupStoragePaths.PostgresDir), BackupStoragePaths.PostgresArchivePattern);
        var sc = BackupStoragePaths.LatestFile(Path.Combine(backupRoot, BackupStoragePaths.ScyllaDir), BackupStoragePaths.ScyllaArchivePattern);
        if (pg is null || sc is null) return null;
        return (pg.FullName, sc.FullName);
    }

    }


