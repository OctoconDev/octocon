using System.Net;
using System.Text.Json;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;
using Interfold.Contracts.Configuration;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// One-shot Docker image update workflow. Backs up the database, pulls new images,
/// recreates the stack, health-checks Postgres + Scylla/Cassandra + the API, and either
/// succeeds cleanly, prints the manual restore recipe on failure, or (with
/// <see cref="BootstrapOptions.AutoRestore"/> / <see cref="UpdateSection.AutoRestoreOnFailure"/>)
/// invokes <see cref="RestorePhase"/> inline against the archives captured in the same run.
/// </summary>
/// <remarks>
/// <para>
/// The pre-update backup is <em>always</em> taken unless the operator passes the
/// <see cref="BootstrapOptions.SkipPreUpdateBackup"/> escape hatch. This is deliberate:
/// image updates can bring a schema-incompatible database version (a Postgres major bump
/// via <c>latest-pg19</c>, a Scylla format change, etc.), and the recovery path assumes
/// a matching snapshot exists on disk.
/// </para>
/// <para>
/// The phase is idempotent for the "nothing changed" case — after the pull it compares
/// image digests against the pre-pull snapshot and short-circuits if every service still
/// references the same image ID. Old backups are still pruned per
/// <see cref="BackupSection.RetainCount"/> even on the no-op path so scheduled updates
/// don't accidentally accumulate archives forever.
/// </para>
/// </remarks>
internal static class UpdateImagesPhase
{
    private static readonly string Phase = BootstrapCommand.UpdateImages.ToPhaseLogName();

    public static async Task<int> RunAsync(BootstrapOptions options, PhaseLogger logger, CancellationToken ct)
    {
        logger.PhaseStart(Phase);

        var configPath = BootstrapArtifactPaths.ResolveConfigPath(options);
        if (!File.Exists(configPath))
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.MissingConfig);
            throw new InvalidOperationException(
                $"update-images requires a populated bootstrap config at {configPath}. " +
                "Run `bootstrap` first.");
        }

        BootstrapConfig config;
        await using (var stream = File.OpenRead(configPath))
        {
            config = await JsonSerializer.DeserializeAsync(
                stream, BootstrapJsonContext.Default.BootstrapConfig, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Failed to parse {configPath}.");
        }

        var composeFile = FindComposeFile(options.OutputDir);
        if (composeFile is null)
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.NoComposeFile);
            throw new InvalidOperationException(
                $"docker-compose.yaml not found under {options.OutputDir}. Run `bootstrap publish` first.");
        }
        logger.Info($"    using compose file {composeFile}");

        // Whitelist precedence: CLI --service beats config.update.services beats "every service".
        // The empty-array sentinel means "pass no service names to docker compose" which
        // compose interprets as "act on every service".
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

        // Snapshot the current digests BEFORE anything else so the "did anything change"
        // comparison in step 6 has a stable baseline. If this fails (e.g. stack isn't up
        // yet) we can't safely diff later, so fail fast rather than silently always-recreate.
        logger.Info("    snapshotting pre-pull image digests");
        var preDigests = await SnapshotImageDigestsAsync(composeFile, logger, ct).ConfigureAwait(false);

        // Pre-update backup. Uses BackupPhase directly so retention + argv shape stay in
        // one place. `all` (both postgres + scylla) is the only sensible choice here — a
        // partial backup couldn't safely feed an auto-restore.
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

        // Cassandra mode: rebuild the local interfold-cassandra:local image before the pull so
        // Dockerfile edits (base image bump, entrypoint tweak, JVM opts) actually take effect
        // via update-images instead of forcing operators to re-run `bootstrap publish`. Docker's
        // layer cache makes this cheap when nothing changed. The matching pull-skip is handled
        // declaratively by PublishPhase.StampCassandraPullPolicyNever, which marks the cassandra
        // service pull_policy: never in the emitted compose file — `docker compose pull` reports
        // it as Skipped instead of failing on the non-registry-backed tag.
        if (ShouldRebuildCassandra(config, services))
        {
            logger.Info("    cassandra mode: rebuilding interfold-cassandra:local before pull");
            await CassandraImagePhase.EnsureBuiltAsync(logger, ct).ConfigureAwait(false);
        }

        // Pull new images. Non-zero exit here means we never left the pre-pull state —
        // no recreate has happened yet, so the stack is safe to leave alone.
        logger.Info("    docker compose pull ...");
        var pullArgs = BuildComposePullArgs(composeFile, services);
        var pull = await ProcessRunner.RunAsync("docker", pullArgs, ct: ct).ConfigureAwait(false);
        if (pull.ExitCode != 0)
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.PullFailed);
            throw new InvalidOperationException(
                $"docker compose pull exited {pull.ExitCode}: {pull.StdErr.Trim()}");
        }
        // Forward BOTH streams on success — docker compose writes machine-readable payloads
        // (rare for `pull`) to stdout but per-service progress (`msg-db Pulling`,
        // `cassandra Skipped`, `msg-db Pulled`) to stderr. Suppressing stderr on success
        // would hide the "which services actually pulled anything" signal from the operator
        // (and from the integration tests that assert on the `Skipped` marker to prove
        // `pull_policy: never` fired for locally-built images like `interfold-cassandra:local`).
        if (!string.IsNullOrWhiteSpace(pull.StdOut)) logger.Info(pull.StdOut.Trim());
        if (!string.IsNullOrWhiteSpace(pull.StdErr)) logger.Info(pull.StdErr.Trim());

        // Compare digests. If nothing changed, we've paid for the backup and the pull
        // (which was a no-op at the layer level) — but we can skip the recreate + health
        // check + downtime, which is the whole point of the diff.
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

        // Recreate. `up -d` is the compose primitive that recreates only containers whose
        // image ID changed — matching our diff above. `restart` is the two-step escape
        // hatch for operators who explicitly disabled RecreateOnUpdate.
        if (recreate)
        {
            logger.Info("    docker compose up -d ...");
            var upArgs = BuildComposeUpArgs(composeFile, services);
            var up = await ProcessRunner.RunAsync("docker", upArgs, ct: ct).ConfigureAwait(false);
            if (up.ExitCode != 0)
            {
                logger.PhaseFail(Phase, PhaseFailureReasons.UpFailed);
                throw new InvalidOperationException(
                    $"docker compose up -d exited {up.ExitCode}: {up.StdErr.Trim()}");
            }
            if (!string.IsNullOrWhiteSpace(up.StdOut)) logger.Info(up.StdOut.Trim());
        }
        else
        {
            logger.Warn("config.update.recreateOnUpdate=false; skipping `up -d`. " +
                        "The new image will NOT take effect until a manual `up -d` is issued.");
        }

        // Health check bounded by healthTimeout. Any failure path leads to the log-and-stop
        // or auto-restore branch — we never leave the stack in a half-updated state without
        // an operator-facing recovery instruction.
        var healthErr = await CheckStackHealthAsync(composeFile, config, healthTimeout, logger, ct).ConfigureAwait(false);
        if (healthErr is not null)
        {
            logger.PhaseFail(Phase, PhaseFailureReasons.HealthCheckFailed);
            await OnHealthCheckFailedAsync(
                options, config, composeFile, healthErr, backupArtifacts, autoRestore, logger, ct)
                .ConfigureAwait(false);
            return 1;
        }

        // Success: prune old backups per retention. Only touches the postgres/ and scylla/
        // subdirectories that the pre-update backup wrote to; on --skip-pre-update-backup
        // we skip pruning entirely because there's no new archive to justify deleting old ones.
        if (backupArtifacts is not null)
        {
            var backupRoot = ResolveBackupRoot(options, config);
            var retainCount = options.BackupRetainOverride ?? config.Backup.RetainCount;
            PruneOldArchives(backupRoot, retainCount, logger);
        }

        logger.PhaseDone(Phase);
        return 0;
    }

    /// <summary>
    /// Resolves the effective service whitelist. CLI <c>--service</c> wins over
    /// <see cref="UpdateSection.Services"/>; both empty means "every service".
    /// Config-sourced values were already validated by <c>ConfigPhase</c>; CLI values bypass
    /// it, so they are checked here against the same
    /// <see cref="ComposeServices.AllValidUpdateServices"/> whitelist.
    /// </summary>
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

    /// <summary>
    /// Cassandra mode uses a locally-built image (<c>interfold-cassandra:local</c>) that has no
    /// registry to pull from. This helper decides whether the current update should rebuild it:
    /// only in cassandra mode, and only when <c>cassandra</c> is in the effective service scope.
    /// Operators who narrow the update with <c>--service msg-db</c> don't get an unrelated
    /// rebuild.
    /// </summary>
    /// <remarks>
    /// The matching pull-side plumbing lives in <see cref="PublishPhase.StampCassandraPullPolicyNever"/>,
    /// which stamps <c>pull_policy: never</c> onto the cassandra service in the emitted compose
    /// file so <c>docker compose pull</c> reports it as <c>Skipped</c> instead of failing on
    /// the non-registry-backed tag.
    /// </remarks>
    internal static bool ShouldRebuildCassandra(BootstrapConfig config, IReadOnlyList<string> effectiveServices)
    {
        if (!CassandraImagePhase.IsCassandraDeployment(config)) return false;
        if (effectiveServices.Count == 0) return true;
        return effectiveServices.Contains(ComposeServices.Cassandra, StringComparer.Ordinal);
    }

    /// <summary>
    /// Builds the <c>docker compose pull</c> argv. When <paramref name="services"/> is
    /// empty the argv ends with just <c>pull</c> (compose semantics: no service names
    /// means "every service in the file"). Internal for unit-test coverage.
    /// </summary>
    internal static IReadOnlyList<string> BuildComposePullArgs(string composeFile, IReadOnlyList<string> services)
    {
        var argv = new List<string> { "compose", "-f", composeFile, "pull" };
        argv.AddRange(services);
        return argv;
    }

    /// <summary>
    /// Builds the <c>docker compose up -d</c> argv. Same "empty = every service"
    /// semantics as <see cref="BuildComposePullArgs"/>.
    /// </summary>
    internal static IReadOnlyList<string> BuildComposeUpArgs(string composeFile, IReadOnlyList<string> services)
    {
        var argv = new List<string> { "compose", "-f", composeFile, "up", "-d" };
        argv.AddRange(services);
        return argv;
    }

    /// <summary>
    /// Builds the <c>docker compose images --format json</c> argv used by the digest
    /// snapshot. Compose v2 emits JSON Lines (one object per container/service).
    /// </summary>
    internal static IReadOnlyList<string> BuildComposeImagesArgs(string composeFile)
    {
        return ["compose", "-f", composeFile, "images", "--format", "json"];
    }

    /// <summary>
    /// Builds the <c>docker compose logs --tail &lt;N&gt; &lt;service&gt;</c> argv used
    /// after a health-check failure to surface the failing container's tail-end logs to
    /// the operator.
    /// </summary>
    internal static IReadOnlyList<string> BuildComposeLogsArgs(string composeFile, string service, int tail)
    {
        return ["compose", "-f", composeFile, "logs", "--tail", tail.ToString(System.Globalization.CultureInfo.InvariantCulture), service];
    }

    /// <summary>
    /// Parses <c>docker compose images --format json</c> output into a service → image ID
    /// map. Compose v2 emits either a single JSON array or JSON Lines depending on the
    /// version; both shapes are accepted. Duplicate service entries (multiple replicas)
    /// collapse to the last-seen image ID — updates always recreate every replica of a
    /// service in lockstep, so a per-replica differentiation isn't meaningful here.
    /// Internal so unit tests can drive a canned JSON payload without invoking docker.
    /// </summary>
    internal static IDictionary<string, string> ParseComposeImagesJson(string json)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return result;

        // Try the JSON-array shape first (older compose plugin versions).
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

        // Fall through to JSON Lines: one object per line, blank lines ignored. This
        // matches what modern (v2.20+) compose plugin emits.
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
                // Skip malformed lines — surfacing them would fail the whole diff on a
                // benign compose stderr leak into stdout, which some older versions do.
            }
        }
        return result;

        static void TryAdd(JsonElement obj, Dictionary<string, string> into)
        {
            if (obj.ValueKind != JsonValueKind.Object) return;
            if (!obj.TryGetProperty("Service", out var svc) || svc.ValueKind != JsonValueKind.String) return;
            var service = svc.GetString();
            if (string.IsNullOrEmpty(service)) return;
            // Prefer "ID" (docker's stable image ID) over "Repository:Tag" — the tag can
            // stay the same across a floating-tag pull, but the ID always shifts on a
            // real image change.
            var id = obj.TryGetProperty("ID", out var idProp) && idProp.ValueKind == JsonValueKind.String
                ? idProp.GetString() ?? string.Empty
                : string.Empty;
            into[service] = id;
        }
    }

    /// <summary>
    /// Compares two service → image-ID maps and returns the services whose image ID
    /// changed (or that appeared/disappeared between snapshots). Ordering is
    /// alphabetical for stable log output.
    /// </summary>
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

    /// <summary>
    /// Runs a bounded probe against Postgres (pg_isready via compose exec), Scylla
    /// (CQL <c>DESCRIBE CLUSTER</c> via compose exec / <c>nodetool status</c> for
    /// Cassandra), and the API (<c>GET /health/ready</c> on the host-mapped HTTP port).
    /// Returns <c>null</c> on success, or a short operator-facing description of what
    /// failed on timeout. The three checks share a single deadline so a Postgres that
    /// takes 90% of the budget doesn't starve the API check.
    /// </summary>
    private static async Task<string?> CheckStackHealthAsync(
        string composeFile, BootstrapConfig config, TimeSpan totalTimeout,
        PhaseLogger logger, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + totalTimeout;
        logger.Info($"    health-check: bounded to {totalTimeout.TotalSeconds:F0}s across postgres+scylla+api");

        // Postgres: pg_isready inside the container. TCP probe — successful means the
        // new image finished its entrypoint boot AND the listener is accepting connections
        // (not just the Unix socket during init).
        var pgErr = await WaitForPostgresReadyAsync(composeFile, deadline, logger, ct).ConfigureAwait(false);
        if (pgErr is not null) return pgErr;

        // Scylla / Cassandra: nodetool status is the leanest probe that doesn't require
        // knowing the app password — it succeeds when the node has finished gossip
        // bootstrap and is UN (Up/Normal).
        var (scyllaService, _) = BackupPhase.ResolveScyllaSeed(config);
        var scErr = await WaitForScyllaReadyAsync(composeFile, scyllaService, deadline, logger, ct).ConfigureAwait(false);
        if (scErr is not null) return scErr;

        // API: HTTP GET /health/ready on the host-mapped HTTP port. Same shape as
        // LaunchPhase's health probe.
        var apiErr = await WaitForApiReadyAsync(config.Ports.ApiHttp, deadline, logger, ct).ConfigureAwait(false);
        if (apiErr is not null) return apiErr;

        logger.Info("    health-check: all three tiers ready");
        return null;
    }

    private static async Task<string?> WaitForPostgresReadyAsync(
        string composeFile, DateTime deadline, PhaseLogger logger, CancellationToken ct)
    {
        var attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            var probe = await ProcessRunner.RunAsync("docker",
                ["compose", "-f", composeFile, "exec", "-T", ComposeServices.Postgres,
                 "pg_isready", "-h", "127.0.0.1", "-p", "5432"],
                ct: ct).ConfigureAwait(false);
            if (probe.ExitCode == 0)
            {
                logger.Info($"    postgres ready after {attempt} probe(s)");
                return null;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }
        return $"postgres ({ComposeServices.Postgres}) did not report ready within the health-check budget";
    }

    private static async Task<string?> WaitForScyllaReadyAsync(
        string composeFile, string service, DateTime deadline, PhaseLogger logger, CancellationToken ct)
    {
        var attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            // `nodetool status` exits 0 once the node is UN and gossip has settled;
            // during startup or when the coordinator can't reach itself yet it exits
            // non-zero. Cheaper than authenticating a CQL session and doesn't need the
            // app password.
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

    private static async Task<string?> WaitForApiReadyAsync(
        int apiHttpPort, DateTime deadline, PhaseLogger logger, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var url = $"http://localhost:{apiHttpPort}{HealthEndpoints.Ready}";
        var attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            try
            {
                var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    logger.Info($"    api ready at {url} after {attempt} attempt(s)");
                    return null;
                }
            }
            catch (HttpRequestException) { /* container may not be listening yet */ }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { /* per-request timeout */ }

            try { await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }
        return $"api did not return 200 at {url} within the health-check budget";
    }

    private static async Task OnHealthCheckFailedAsync(
        BootstrapOptions options, BootstrapConfig config, string composeFile,
        string healthErr, (string PostgresArchive, string ScyllaArchive)? backupArtifacts,
        bool autoRestore, PhaseLogger logger, CancellationToken ct)
    {
        logger.Error($"health check failed: {healthErr}");

        // Best-effort: dump the failing tier's logs so operators have a diagnosis without
        // having to shell into the box.
        var suspects = new[] { ComposeServices.Postgres, ComposeServices.ScyllaSingle, ComposeServices.ScyllaNam, ComposeServices.Cassandra, ComposeServices.InterfoldApi, ComposeServices.OctoconWeb };
        foreach (var svc in suspects)
        {
            var logsArgs = BuildComposeLogsArgs(composeFile, svc, 200);
            var logs = await ProcessRunner.RunAsync("docker", logsArgs, ct: ct).ConfigureAwait(false);
            if (logs.ExitCode == 0 && !string.IsNullOrWhiteSpace(logs.StdOut))
            {
                logger.Warn($"--- {svc} logs (last 200 lines) ---");
                Console.Error.WriteLine(logs.StdOut);
            }
        }

        // Compose down: leave the stack in a clean stopped state so a subsequent
        // restore or re-attempt doesn't fight with half-recreated containers.
        logger.Info("    docker compose down ...");
        var down = await ProcessRunner.RunAsync("docker",
            ["compose", "-f", composeFile, "down"], ct: ct).ConfigureAwait(false);
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

        // No auto-restore: print the exact copy-pasteable command.
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

    /// <summary>
    /// Picks the newest postgres and scylla archive paths under the resolved backup
    /// root, based on file mtime. Called immediately after the pre-update
    /// <see cref="BackupPhase"/> invocation so the "newest" archive is exactly the one
    /// this run produced. Returns null if either component is missing on disk (which
    /// would indicate a broken pre-update backup — the caller downgrades auto-restore
    /// to a warning in that case).
    /// </summary>
    internal static (string PostgresArchive, string ScyllaArchive)? ResolveLatestBackupArtifacts(
        BootstrapOptions options, BootstrapConfig config)
    {
        var backupRoot = ResolveBackupRoot(options, config);
        var pg = LatestFile(Path.Combine(backupRoot, BackupStoragePaths.PostgresDir), BackupStoragePaths.PostgresArchivePattern);
        var sc = LatestFile(Path.Combine(backupRoot, BackupStoragePaths.ScyllaDir), BackupStoragePaths.ScyllaArchivePattern);
        if (pg is null || sc is null) return null;
        return (pg.FullName, sc.FullName);
    }

    private static FileInfo? LatestFile(string dir, string pattern)
    {
        if (!Directory.Exists(dir)) return null;
        return new DirectoryInfo(dir)
            .EnumerateFiles(pattern, SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string ResolveBackupRoot(BootstrapOptions options, BootstrapConfig config)
    {
        // Mirrors BackupPhase.ResolveBackupRoot; kept in sync manually because both phases
        // read the same option precedence rules.
        if (!string.IsNullOrWhiteSpace(options.BackupDirOverride)) return Path.GetFullPath(options.BackupDirOverride);
        if (!string.IsNullOrWhiteSpace(config.Backup.Directory)) return Path.GetFullPath(config.Backup.Directory);
        return Path.Combine(options.OutputDir, "backups");
    }

    private static void PruneOldArchives(string backupRoot, int retainCount, PhaseLogger logger)
    {
        // Reuses BackupRetention.Prune (pure logic, unit-tested separately in
        // BackupRetentionTests). We prune both component directories because the caller
        // just wrote to both.
        foreach (var (component, pattern) in new[] { (BackupStoragePaths.PostgresDir, BackupStoragePaths.PostgresArchivePattern), (BackupStoragePaths.ScyllaDir, BackupStoragePaths.ScyllaArchivePattern) })
        {
            var componentDir = Path.Combine(backupRoot, component);
            if (!Directory.Exists(componentDir)) continue;
            var files = new DirectoryInfo(componentDir).EnumerateFiles(pattern, SearchOption.TopDirectoryOnly);
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
    }

    private static string? FindComposeFile(string outputDir) => BootstrapArtifactPaths.FindComposeFile(outputDir);
}
