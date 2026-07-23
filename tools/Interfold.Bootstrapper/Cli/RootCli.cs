using System.CommandLine;
using System.CommandLine.Parsing;
using Interfold.Bootstrapper.Phases;

namespace Interfold.Bootstrapper.Cli;

/// <summary>
/// Entry point and command tree for the bootstrapper CLI.
/// Commands: <c>bootstrap</c> (default), <c>publish</c>, <c>up</c>, <c>rotate-secrets</c>,
/// <c>rotate-certs</c>, <c>show-trust</c>, <c>backup</c>, <c>install-service</c>,
/// <c>update-images</c>, <c>restore</c>.
/// </summary>
public static class RootCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        var root = BuildRoot();
        var parseResult = root.Parse(args);
        return await parseResult.InvokeAsync().ConfigureAwait(false);
    }

    private static RootCommand BuildRoot()
    {
        var configOpt = new Option<string?>("--config", "-c")
        {
            Description = "Path to interfold.bootstrap.json. If omitted, the bootstrapper prompts interactively (tty only)."
        };
        var outputDirOpt = new Option<string>("--output-dir", "-o")
        {
            Description = "Root directory for emitted artifacts (compose, certs, secrets).",
            DefaultValueFactory = _ => "./deploy"
        };
        var skipPrereqsOpt = new Option<bool>("--skip-prereqs")
        {
            Description = "Skip the prerequisites phase (Docker/openssl install, AIO sysctl). Use on re-runs."
        };
        var nonInteractiveOpt = new Option<bool>("--non-interactive")
        {
            Description = "Fail rather than prompt when config values are missing."
        };
        // Hidden testability flags - not surfaced in help but accepted by the parser.
        var faultInjectOpt = new Option<string?>("--fault-inject") { Hidden = true };
        var printPhaseStatusOpt = new Option<bool>("--print-phase-status") { Hidden = true };

        // --- backup-specific options ---
        // Component selector: postgres = pg_dump only; scylla = nodetool snapshot only;
        // all = both, in that order. Invalid values are caught by BackupPhase, not here,
        // so the error reads "unknown component 'foo'" with the same shape every consumer
        // sees rather than System.CommandLine's generic parse failure.
        var backupComponentOpt = new Option<string>("--component")
        {
            Description = "Which database to back up: 'postgres', 'scylla', or 'all'. Defaults to 'all'.",
            DefaultValueFactory = _ => "all"
        };
        var backupRetainOpt = new Option<int?>("--retain")
        {
            Description = "Override config.backup.retainCount for this run. Must be in [1..1000]."
        };
        var backupDirOpt = new Option<string?>("--backup-dir")
        {
            Description = "Override config.backup.directory for this run. Must be an absolute path."
        };

        // --- install-service-specific options ---
        // When the flag is omitted on the CLI the default falls back to the matching config
        // value (BackupSection.AutostartServer / BackupSection.Enabled); we accept a tri-state
        // via the System.CommandLine bool option's "specified" check on the parse result.
        var enableAutostartOpt = new Option<bool>("--enable-autostart")
        {
            Description = "After writing units, run `systemctl enable --now interfold.service`. Overrides config.backup.autostartServer."
        };
        var enableBackupTimerOpt = new Option<bool>("--enable-backup-timer")
        {
            Description = "After writing units, run `systemctl enable --now interfold-backup.timer`. Overrides config.backup.enabled."
        };
        var systemdUnitDirOpt = new Option<string?>("--systemd-unit-dir")
        {
            Description = "Override the systemd unit installation directory (default: /etc/systemd/system). Used by integration tests."
        };
        var binaryPathOpt = new Option<string?>("--binary-path")
        {
            Description = "Override the bootstrapper binary path baked into interfold-backup.service (default: AppContext.BaseDirectory/interfold-bootstrap)."
        };

        // --- update-images-specific options ---
        // Rollback default is manual: --auto-restore opts into the destructive path.
        // --skip-pre-update-backup is an escape hatch for operators who took a fresh
        // manual snapshot immediately before running update-images and don't want the
        // duplicate archive.
        var autoRestoreOpt = new Option<bool>("--auto-restore")
        {
            Description = "On health-check failure, invoke `restore --force` against the pre-update backup instead of just printing the recipe. Overrides config.update.autoRestoreOnFailure."
        };
        var skipPreUpdateBackupOpt = new Option<bool>("--skip-pre-update-backup")
        {
            Description = "DANGEROUS: skip the pre-update database backup. Use only when a fresh backup already exists on disk. Off by default."
        };
        var updateServicesOpt = new Option<string[]>("--service")
        {
            Description = "Compose services to pull + recreate. Repeatable; e.g. --service interfold-api --service octocon-web. Empty (the default) means every service.",
            AllowMultipleArgumentsPerToken = true,
        };
        var healthCheckTimeoutOpt = new Option<int?>("--health-check-timeout")
        {
            Description = "Override config.update.healthCheckTimeoutSeconds for this run. Bounded [1..3600]."
        };

        // --- restore-specific options ---
        // Two path selectors + a mtime resolver + a --force to skip the confirmation
        // prompt. Restoring nothing (all three archive selectors omitted with --restore-latest
        // false) is caught by RestorePhase, not here.
        var restorePostgresOpt = new Option<string?>("--restore-postgres")
        {
            Description = "Path to a specific pg_dump archive (custom format) to restore. Mutually exclusive with --restore-latest for the postgres component."
        };
        var restoreScyllaOpt = new Option<string?>("--restore-scylla")
        {
            Description = "Path to a specific scylla .tar.gz archive to restore. Mutually exclusive with --restore-latest for the scylla component."
        };
        var restoreLatestOpt = new Option<bool>("--restore-latest")
        {
            Description = "Pick the newest archive by mtime under {backupRoot}/{component}/ for every component that wasn't explicitly named on the CLI."
        };
        var restoreForceOpt = new Option<bool>("--force")
        {
            Description = "Skip the interactive 'this will wipe your data volumes' confirmation. Required in non-interactive mode."
        };

        var root = new RootCommand(
            "Interfold self-hosting bootstrapper. Brings a fresh Linux box from 'git clone' to a running stack.");

        // ---------- bootstrap (default) ----------
        var bootstrapCmd = new Command("bootstrap",
            "Run all phases: prereqs -> config -> secrets -> certs -> publish -> launch.");
        AddSharedOptions(bootstrapCmd, configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt);
        bootstrapCmd.SetAction((parse, ct) => InvokeAsync(BootstrapCommand.Bootstrap, parse,
            configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt,
            rotateSecrets: false, rotateCerts: false, ct));
        root.Subcommands.Add(bootstrapCmd);

        // ---------- publish (compose-only, no docker compose up) ----------
        var publishCmd = new Command("publish",
            "Run config + secrets + certs + compose-publish without launching the stack.");
        AddSharedOptions(publishCmd, configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt);
        publishCmd.SetAction((parse, ct) => InvokeAsync(BootstrapCommand.Publish, parse,
            configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt,
            rotateSecrets: false, rotateCerts: false, ct));
        root.Subcommands.Add(publishCmd);

        // ---------- up (launch only against existing compose) ----------
        var upCmd = new Command("up",
            "Launch an already-generated compose stack: docker compose up -d + health wait.");
        AddSharedOptions(upCmd, configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt);
        upCmd.SetAction((parse, ct) => InvokeAsync(BootstrapCommand.Up, parse,
            configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt,
            rotateSecrets: false, rotateCerts: false, ct));
        root.Subcommands.Add(upCmd);

        // ---------- rotate-secrets ----------
        var rotateSecretsCmd = new Command("rotate-secrets",
            "Regenerate DB/admin passwords, encryption keypair, and pepper. Re-emits compose + restarts API.");
        AddSharedOptions(rotateSecretsCmd, configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt);
        rotateSecretsCmd.SetAction((parse, ct) => InvokeAsync(BootstrapCommand.RotateSecrets, parse,
            configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt,
            rotateSecrets: true, rotateCerts: false, ct));
        root.Subcommands.Add(rotateSecretsCmd);

        // ---------- rotate-certs ----------
        var rotateCertsCmd = new Command("rotate-certs",
            "Regenerate root CA + leaf cert. Re-installs into the system trust store.");
        AddSharedOptions(rotateCertsCmd, configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt);
        rotateCertsCmd.SetAction((parse, ct) => InvokeAsync(BootstrapCommand.RotateCerts, parse,
            configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt,
            rotateSecrets: false, rotateCerts: true, ct));
        root.Subcommands.Add(rotateCertsCmd);

        // ---------- show-trust ----------
        var showTrustCmd = new Command("show-trust",
            "Print the root CA path, SHA-256 fingerprint, expiry, and fetch/verify snippet. Read-only.");
        AddSharedOptions(showTrustCmd, configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt);
        showTrustCmd.SetAction((parse, ct) => InvokeAsync(BootstrapCommand.ShowTrust, parse,
            configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt,
            rotateSecrets: false, rotateCerts: false, ct));
        root.Subcommands.Add(showTrustCmd);

        // ---------- backup ----------
        var backupCmd = new Command("backup",
            "Snapshot Postgres + Scylla/Cassandra to {outputDir}/backups/ and prune older copies. Idempotent.");
        AddSharedOptions(backupCmd, configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt);
        backupCmd.Options.Add(backupComponentOpt);
        backupCmd.Options.Add(backupRetainOpt);
        backupCmd.Options.Add(backupDirOpt);
        backupCmd.SetAction((parse, ct) => InvokeAsync(BootstrapCommand.Backup, parse,
            configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt,
            rotateSecrets: false, rotateCerts: false, ct,
            backupComponentOpt: backupComponentOpt,
            backupRetainOpt: backupRetainOpt,
            backupDirOpt: backupDirOpt));
        root.Subcommands.Add(backupCmd);

        // ---------- install-service ----------
        var installServiceCmd = new Command("install-service",
            "Install systemd units for boot-up autostart + scheduled backups. Writes to /etc/systemd/system/.");
        AddSharedOptions(installServiceCmd, configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt);
        installServiceCmd.Options.Add(enableAutostartOpt);
        installServiceCmd.Options.Add(enableBackupTimerOpt);
        installServiceCmd.Options.Add(systemdUnitDirOpt);
        installServiceCmd.Options.Add(binaryPathOpt);
        installServiceCmd.SetAction((parse, ct) => InvokeAsync(BootstrapCommand.InstallService, parse,
            configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt,
            rotateSecrets: false, rotateCerts: false, ct,
            enableAutostartOpt: enableAutostartOpt,
            enableBackupTimerOpt: enableBackupTimerOpt,
            systemdUnitDirOpt: systemdUnitDirOpt,
            binaryPathOpt: binaryPathOpt));
        root.Subcommands.Add(installServiceCmd);

        // ---------- update-images ----------
        var updateImagesCmd = new Command("update-images",
            "Backup DB, `docker compose pull`, `up -d`, health-check, then either succeed, print restore recipe on failure, or `--auto-restore`.");
        AddSharedOptions(updateImagesCmd, configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt);
        updateImagesCmd.Options.Add(autoRestoreOpt);
        updateImagesCmd.Options.Add(skipPreUpdateBackupOpt);
        updateImagesCmd.Options.Add(updateServicesOpt);
        updateImagesCmd.Options.Add(healthCheckTimeoutOpt);
        updateImagesCmd.Options.Add(backupDirOpt);
        updateImagesCmd.Options.Add(backupRetainOpt);
        updateImagesCmd.SetAction((parse, ct) => InvokeAsync(BootstrapCommand.UpdateImages, parse,
            configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt,
            rotateSecrets: false, rotateCerts: false, ct,
            backupRetainOpt: backupRetainOpt,
            backupDirOpt: backupDirOpt,
            autoRestoreOpt: autoRestoreOpt,
            skipPreUpdateBackupOpt: skipPreUpdateBackupOpt,
            updateServicesOpt: updateServicesOpt,
            healthCheckTimeoutOpt: healthCheckTimeoutOpt));
        root.Subcommands.Add(updateImagesCmd);

        // ---------- restore ----------
        var restoreCmd = new Command("restore",
            "Restore DB state from backup archives. Destructive — requires --force in non-interactive mode.");
        AddSharedOptions(restoreCmd, configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt);
        restoreCmd.Options.Add(restorePostgresOpt);
        restoreCmd.Options.Add(restoreScyllaOpt);
        restoreCmd.Options.Add(restoreLatestOpt);
        restoreCmd.Options.Add(restoreForceOpt);
        restoreCmd.Options.Add(backupDirOpt);
        restoreCmd.SetAction((parse, ct) => InvokeAsync(BootstrapCommand.Restore, parse,
            configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt,
            rotateSecrets: false, rotateCerts: false, ct,
            backupDirOpt: backupDirOpt,
            restorePostgresOpt: restorePostgresOpt,
            restoreScyllaOpt: restoreScyllaOpt,
            restoreLatestOpt: restoreLatestOpt,
            restoreForceOpt: restoreForceOpt));
        root.Subcommands.Add(restoreCmd);

        // No subcommand -> default to `bootstrap`.
        AddSharedOptions(root, configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt);
        root.SetAction((parse, ct) => InvokeAsync(BootstrapCommand.Bootstrap, parse,
            configOpt, outputDirOpt, skipPrereqsOpt, nonInteractiveOpt, faultInjectOpt, printPhaseStatusOpt,
            rotateSecrets: false, rotateCerts: false, ct));

        return root;
    }

    private static void AddSharedOptions(
        Command target,
        Option<string?> configOpt,
        Option<string> outputDirOpt,
        Option<bool> skipPrereqsOpt,
        Option<bool> nonInteractiveOpt,
        Option<string?> faultInjectOpt,
        Option<bool> printPhaseStatusOpt)
    {
        target.Options.Add(configOpt);
        target.Options.Add(outputDirOpt);
        target.Options.Add(skipPrereqsOpt);
        target.Options.Add(nonInteractiveOpt);
        target.Options.Add(faultInjectOpt);
        target.Options.Add(printPhaseStatusOpt);
    }

    private static async Task<int> InvokeAsync(
        BootstrapCommand command,
        ParseResult parse,
        Option<string?> configOpt,
        Option<string> outputDirOpt,
        Option<bool> skipPrereqsOpt,
        Option<bool> nonInteractiveOpt,
        Option<string?> faultInjectOpt,
        Option<bool> printPhaseStatusOpt,
        bool rotateSecrets,
        bool rotateCerts,
        CancellationToken ct,
        Option<string>? backupComponentOpt = null,
        Option<int?>? backupRetainOpt = null,
        Option<string?>? backupDirOpt = null,
        Option<bool>? enableAutostartOpt = null,
        Option<bool>? enableBackupTimerOpt = null,
        Option<string?>? systemdUnitDirOpt = null,
        Option<string?>? binaryPathOpt = null,
        Option<bool>? autoRestoreOpt = null,
        Option<bool>? skipPreUpdateBackupOpt = null,
        Option<string[]>? updateServicesOpt = null,
        Option<int?>? healthCheckTimeoutOpt = null,
        Option<string?>? restorePostgresOpt = null,
        Option<string?>? restoreScyllaOpt = null,
        Option<bool>? restoreLatestOpt = null,
        Option<bool>? restoreForceOpt = null)
    {
        var options = new BootstrapOptions(
            Command: command,
            ConfigPath: parse.GetValue(configOpt),
            OutputDir: Path.GetFullPath(parse.GetValue(outputDirOpt) ?? "./deploy"),
            SkipPrereqs: parse.GetValue(skipPrereqsOpt),
            RotateSecrets: rotateSecrets,
            RotateCerts: rotateCerts,
            NonInteractive: parse.GetValue(nonInteractiveOpt),
            FaultInject: parse.GetValue(faultInjectOpt),
            PrintPhaseStatus: parse.GetValue(printPhaseStatusOpt),
            BackupComponent: backupComponentOpt is null ? "all" : (parse.GetValue(backupComponentOpt) ?? "all"),
            BackupRetainOverride: backupRetainOpt is null ? null : parse.GetValue(backupRetainOpt),
            BackupDirOverride: backupDirOpt is null ? null : parse.GetValue(backupDirOpt),
            EnableAutostart: enableAutostartOpt is not null && parse.GetValue(enableAutostartOpt),
            EnableBackupTimer: enableBackupTimerOpt is not null && parse.GetValue(enableBackupTimerOpt),
            SystemdUnitDir: systemdUnitDirOpt is null ? null : parse.GetValue(systemdUnitDirOpt),
            BinaryPathOverride: binaryPathOpt is null ? null : parse.GetValue(binaryPathOpt),
            AutoRestore: autoRestoreOpt is not null && parse.GetValue(autoRestoreOpt),
            SkipPreUpdateBackup: skipPreUpdateBackupOpt is not null && parse.GetValue(skipPreUpdateBackupOpt),
            UpdateServices: updateServicesOpt is null ? null : parse.GetValue(updateServicesOpt),
            HealthCheckTimeoutOverride: healthCheckTimeoutOpt is null ? null : parse.GetValue(healthCheckTimeoutOpt),
            RestorePostgresArchive: restorePostgresOpt is null ? null : parse.GetValue(restorePostgresOpt),
            RestoreScyllaArchive: restoreScyllaOpt is null ? null : parse.GetValue(restoreScyllaOpt),
            RestoreLatest: restoreLatestOpt is not null && parse.GetValue(restoreLatestOpt),
            RestoreForce: restoreForceOpt is not null && parse.GetValue(restoreForceOpt));

        var logger = new PhaseLogger(options);

        try
        {
            return await Orchestrator.RunAsync(options, logger, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.Error("Cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            logger.Error(ex.Message);
            return 1;
        }
    }
}
