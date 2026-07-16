using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// Dispatches the per-command phase sequence. Phases are designed to be idempotent — second runs
/// short-circuit unless an explicit rotate flag is passed.
/// </summary>
internal static class Orchestrator
{
    public static async Task<int> RunAsync(BootstrapOptions options, PhaseLogger logger, CancellationToken ct)
    {
        // Materialise embedded support files (Scylla rackdc properties, nginx envsubst template,
        // ensure-host-aio.sh) next to the binary on first run. Runs before any phase so every
        // downstream consumer — PublishPhase's compose emit, AppHost's WithBindMount resolution,
        // and operator-facing recovery scripts — sees the same on-disk layout. Power users who
        // dropped their own copy keep it; only missing files are written. See
        // Util/EmbeddedSupportFiles.cs for the manifest discovery rules.
        EmbeddedSupportFiles.EnsureExtracted(AppContext.BaseDirectory, logger);

        // --- show-trust short-circuit ---
        // Pure read: load rootCA.crt / rootCA.sha256.txt and emit the operator-facing trust
        // block. Deliberately bypasses prereqs/config/secrets/certs/publish so the command is
        // safe to run anywhere (including against a sealed deploy/ tree on an ops jumphost),
        // and so it succeeds even when interfold.bootstrap.json is unavailable.
        if (options.Command == BootstrapCommand.ShowTrust)
        {
            var certsDir = Path.Combine(options.OutputDir, "certs");
            var rootCrtPath = Path.Combine(certsDir, "rootCA.crt");
            var rootFingerprintPath = Path.Combine(certsDir, "rootCA.sha256.txt");

            if (!File.Exists(rootCrtPath))
            {
                logger.Error($"rootCA.crt not found at {rootCrtPath}. Run `bootstrap` (or `bootstrap rotate-certs`) first.");
                return 1;
            }

            CertificatePhase.PrintTrustInfo(rootCrtPath, rootFingerprintPath, logger);
            return 0;
        }

        // --- backup short-circuit ---
        // Snapshots Postgres + Scylla/Cassandra against an already-running compose stack.
        // Loads (does not generate) config + secrets from disk. Like show-trust, this skips
        // the heavyweight host-mutating phases entirely because backup is a runtime
        // operation that the operator (or systemd timer) expects to be quick and idempotent.
        if (options.Command == BootstrapCommand.Backup)
        {
            return await BackupPhase.RunAsync(options, logger, ct).ConfigureAwait(false);
        }

        // --- install-service short-circuit ---
        // Renders the embedded systemd unit templates into /etc/systemd/system/ and
        // optionally runs systemctl enable --now. Reads config from disk for the backup
        // schedule + autostart toggles, but never generates secrets or invokes Aspire.
        if (options.Command == BootstrapCommand.InstallService)
        {
            return await SystemdInstallPhase.RunAsync(options, logger, ct).ConfigureAwait(false);
        }

        // --- update-images short-circuit ---
        // Wraps a pre-update backup, `docker compose pull`, `up -d`, and a bounded
        // health check. On failure, either prints the restore recipe or (with
        // --auto-restore) invokes RestorePhase inline. Skips prereqs/config/secrets
        // for the same reason backup does — this is a runtime operation.
        if (options.Command == BootstrapCommand.UpdateImages)
        {
            return await UpdateImagesPhase.RunAsync(options, logger, ct).ConfigureAwait(false);
        }

        // --- restore short-circuit ---
        // Streams pg_restore + scylla `docker cp` against archives on disk. Destructive;
        // requires --force in non-interactive mode. Reads config + secrets, never
        // generates or rotates them.
        if (options.Command == BootstrapCommand.Restore)
        {
            return await RestorePhase.RunAsync(options, logger, ct).ConfigureAwait(false);
        }

        // Phase sequence per command:
        //   bootstrap       prereqs -> config -> secrets -> certs -> publish -> db-init -> launch
        //   publish         config  -> secrets -> certs -> publish
        //   up                                                                    -> launch
        //   rotate-secrets  config  -> secrets (force) -> publish -> db-init -> launch
        //   rotate-certs    config  -> certs   (force) -> publish -> db-init -> launch
        //
        // `publish` is intentionally artifact-only: no docker calls, no live infrastructure
        // changes. That lets tests run it as a fast smoke check and lets operators inspect
        // generated compose / .env before deciding to commit. The db-init phase below owns the
        // admin / internal.secrets bootstrap that previously lived in the pg-bootstrap-auth /
        // scylla-bootstrap-auth init containers; it runs for any command that subsequently
        // hits `launch` (bootstrap, rotate-secrets, rotate-certs). The phase is itself
        // idempotent - a rerun against an already-initialised cluster short-circuits via
        // PostgresAlreadyInitializedAsync / ScyllaAlreadyInitializedAsync. We run it on
        // rotate-certs even though that command doesn't touch DB state because the operator's
        // expectation is "the stack still works after the call"; if their DB volume happens to
        // be empty (clean rebuild, etc) we still want launch to succeed.

        // --- Prerequisites (only the `bootstrap` command runs these by default) ---
        if (options.Command == BootstrapCommand.Bootstrap && !options.SkipPrereqs)
        {
            await PrerequisitesPhase.RunAsync(options, logger, ct).ConfigureAwait(false);
            if (HaltAfter(options, BootstrapPhase.Prereqs, logger)) return 0;
        }

        // --- Config (skipped only for raw `up`) ---
        BootstrapConfig? config = null;
        if (options.Command != BootstrapCommand.Up)
        {
            config = await ConfigPhase.RunAsync(options, logger, ct).ConfigureAwait(false);
            if (HaltAfter(options, BootstrapPhase.Config, logger)) return 0;
        }

        // --- Secrets ---
        GeneratedSecrets? secrets = null;
        if (options.Command != BootstrapCommand.Up && options.Command != BootstrapCommand.RotateCerts)
        {
            secrets = await SecretsPhase.RunAsync(options, config!, logger, ct).ConfigureAwait(false);
            if (HaltAfter(options, BootstrapPhase.Secrets, logger)) return 0;
        }
        else if (options.Command == BootstrapCommand.RotateCerts)
        {
            // Cert rotation still needs the leaf PFX password from the secrets file; load read-only.
            secrets = SecretsPhase.LoadExisting(options);
        }

        // --- Certificates ---
        if (options.Command != BootstrapCommand.Up && options.Command != BootstrapCommand.RotateSecrets)
        {
            await CertificatePhase.RunAsync(options, config!, secrets!, logger, ct).ConfigureAwait(false);
            if (HaltAfter(options, BootstrapPhase.Certs, logger)) return 0;
        }

        // --- Compose publish ---
        if (options.Command != BootstrapCommand.Up)
        {
            await PublishPhase.RunAsync(options, config!, secrets!, logger, ct).ConfigureAwait(false);
            if (HaltAfter(options, BootstrapPhase.Publish, logger)) return 0;
        }

        // --- Firebase inputs (optional; feeds internal.secrets seeding below) ---
        // Runs on the same commands that eventually hit db-init so the parsed inputs are
        // ready in time for PostgresSeeder to insert them alongside the OAuth / JWT rows.
        // Skipped on `up` (no db-init runs there) and on `publish` (artifact-only). Empty
        // paths in config.Firebase are the supported "no push configured" shape and produce
        // an empty FirebaseSeedInputs — see FirebasePhase for the fail-fast rules.
        FirebaseSeedInputs firebase = FirebaseSeedInputs.Empty;
        if (options.Command is BootstrapCommand.Bootstrap
            or BootstrapCommand.RotateSecrets
            or BootstrapCommand.RotateCerts)
        {
            firebase = await FirebasePhase.RunAsync(options, config!, logger, ct).ConfigureAwait(false);
            if (HaltAfter(options, BootstrapPhase.Firebase, logger)) return 0;
        }

        // --- Database init (admin role + internal.secrets seeding) ---
        // Runs for any command that subsequently invokes launch (bootstrap, rotate-secrets,
        // rotate-certs). `publish` stays artifact-only and skips this phase. The phase is
        // idempotent: a subsequent run against an already-initialised cluster short-circuits
        // via PostgresAlreadyInitializedAsync / ScyllaAlreadyInitializedAsync.
        if (options.Command is BootstrapCommand.Bootstrap
            or BootstrapCommand.RotateSecrets
            or BootstrapCommand.RotateCerts)
        {
            await DatabaseInitPhase.RunAsync(options, config!, secrets!, firebase, logger, ct).ConfigureAwait(false);
            if (HaltAfter(options, BootstrapPhase.DbInit, logger)) return 0;
        }

        // --- Launch ---
        if (options.Command is BootstrapCommand.Bootstrap or BootstrapCommand.Up
            or BootstrapCommand.RotateSecrets or BootstrapCommand.RotateCerts)
        {
            await LaunchPhase.RunAsync(options, logger, ct).ConfigureAwait(false);
            if (HaltAfter(options, BootstrapPhase.Launch, logger)) return 0;
        }

        return 0;
    }

    private static bool HaltAfter(BootstrapOptions options, BootstrapPhase phase, PhaseLogger logger)
    {
        if (!string.Equals(options.FaultInject, phase.ToFaultInjectToken(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        logger.Warn($"--fault-inject={phase.ToFaultInjectToken()} triggered; exiting before next phase.");
        // Tests use the non-zero process exit (Environment.Exit) elsewhere; here we return 0 because the
        // orchestrator caller decides what to do. The test scenario kills the process with SIGKILL, so
        // this code path is mostly a safety net for `--fault-inject` style assertions in local debugging.
        return true;
    }
}
