using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// Dispatches the per-command phase sequence. Phases are designed to be idempotent — second runs
/// short-circuit unless an explicit rotate flag is passed.
/// </summary>
internal static class Orchestrator
{
    public static async Task<int> RunAsync(BootstrapOptions options, PhaseLogger logger, CancellationToken ct)
    {
        // show-trust: pure read, skips prereqs/config/secrets/certs/publish so it's safe on a
        // sealed deploy tree without interfold.bootstrap.json.
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

        // Runtime short-circuits (backup, install-service, update-images, restore): skip the
        // heavyweight host-mutating phases; they only read config + secrets from disk.
        if (options.Command == BootstrapCommand.Backup)
        {
            return await BackupPhase.RunAsync(options, logger, ct).ConfigureAwait(false);
        }
        if (options.Command == BootstrapCommand.InstallService)
        {
            return await SystemdInstallPhase.RunAsync(options, logger, ct).ConfigureAwait(false);
        }
        if (options.Command == BootstrapCommand.UpdateImages)
        {
            return await UpdateImagesPhase.RunAsync(options, logger, ct).ConfigureAwait(false);
        }
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
        // `publish` is artifact-only so tests can run it as a smoke check and operators can
        // inspect the compose/.env before committing. db-init is idempotent (short-circuits via
        // PostgresAlreadyInitializedAsync / ScyllaAlreadyInitializedAsync) and runs on
        // rotate-certs too — clean-rebuild edge case where the DB volume is empty.

        if (options.Command == BootstrapCommand.Bootstrap && !options.SkipPrereqs)
        {
            await PrerequisitesPhase.RunAsync(options, logger, ct).ConfigureAwait(false);
            if (HaltAfter(options, BootstrapPhase.Prereqs, logger)) return 0;
        }

        BootstrapConfig? config = null;
        if (options.Command != BootstrapCommand.Up)
        {
            config = await ConfigPhase.RunAsync(options, logger, ct).ConfigureAwait(false);
            if (HaltAfter(options, BootstrapPhase.Config, logger)) return 0;
        }

        GeneratedSecrets? secrets = null;
        if (options.Command != BootstrapCommand.Up && options.Command != BootstrapCommand.RotateCerts)
        {
            secrets = await SecretsPhase.RunAsync(options, config!, logger, ct).ConfigureAwait(false);
            if (HaltAfter(options, BootstrapPhase.Secrets, logger)) return 0;
        }
        else if (options.Command == BootstrapCommand.RotateCerts)
        {
            // rotate-certs still needs the leaf PFX password — read-only.
            secrets = SecretsPhase.LoadExisting(options);
        }

        if (options.Command != BootstrapCommand.Up && options.Command != BootstrapCommand.RotateSecrets)
        {
            await CertificatePhase.RunAsync(options, config!, secrets!, logger, ct).ConfigureAwait(false);
            if (HaltAfter(options, BootstrapPhase.Certs, logger)) return 0;
        }

        if (options.Command != BootstrapCommand.Up)
        {
            await PublishPhase.RunAsync(options, config!, secrets!, logger, ct).ConfigureAwait(false);
            if (HaltAfter(options, BootstrapPhase.Publish, logger)) return 0;
        }

        // Firebase runs on db-init-bound commands so its parsed inputs land alongside the
        // OAuth/JWT rows PostgresSeeder inserts. Empty paths produce Empty inputs (no push).
        FirebaseSeedInputs firebase = FirebaseSeedInputs.Empty;
        if (options.Command is BootstrapCommand.Bootstrap
            or BootstrapCommand.RotateSecrets
            or BootstrapCommand.RotateCerts)
        {
            firebase = await FirebasePhase.RunAsync(options, config!, logger, ct).ConfigureAwait(false);
            if (HaltAfter(options, BootstrapPhase.Firebase, logger)) return 0;
        }

        if (options.Command is BootstrapCommand.Bootstrap
            or BootstrapCommand.RotateSecrets
            or BootstrapCommand.RotateCerts)
        {
            await DatabaseInitPhase.RunAsync(options, config!, secrets!, firebase, logger, ct).ConfigureAwait(false);
            if (HaltAfter(options, BootstrapPhase.DbInit, logger)) return 0;
        }

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
        // Return 0 — tests do SIGKILL elsewhere; the orchestrator caller decides the exit code.
        return true;
    }
}
