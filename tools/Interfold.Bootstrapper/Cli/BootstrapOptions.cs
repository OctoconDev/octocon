namespace Interfold.Bootstrapper.Cli;

/// <summary>
/// Process-wide invocation options resolved from the CLI. Phases receive this record and
/// dispatch based on the flags rather than reading global state.
/// </summary>
/// <param name="Command">Which subcommand was invoked.</param>
/// <param name="ConfigPath">Path to <c>interfold.bootstrap.json</c>. If null, the bootstrapper prompts interactively (in tty mode).</param>
/// <param name="OutputDir">Root directory for emitted artifacts (compose, certs, secrets).</param>
/// <param name="SkipPrereqs">If true, the prerequisites phase is bypassed. Useful for re-runs after Docker is installed.</param>
/// <param name="RotateSecrets">If true, the secrets phase regenerates everything even if a secrets file already exists.</param>
/// <param name="RotateCerts">If true, the certificate phase regenerates the root CA and leaf cert.</param>
/// <param name="NonInteractive">If true, missing config values are an error rather than a prompt.</param>
/// <param name="FaultInject">Hidden testability hook: exits 1 immediately after the named phase (e.g. <c>after-secrets</c>).</param>
/// <param name="PrintPhaseStatus">Hidden testability hook: emit <c>phase=name status=skipped reason=...</c> lines on stderr.</param>
/// <param name="BackupComponent">For <see cref="BootstrapCommand.Backup"/>: which DB to snapshot. One of <c>postgres</c>, <c>scylla</c>, <c>all</c>. Defaults to <c>all</c>.</param>
/// <param name="BackupRetainOverride">For <see cref="BootstrapCommand.Backup"/>: optional CLI override for <c>config.backup.retainCount</c>. Null means "use the config value".</param>
/// <param name="BackupDirOverride">For <see cref="BootstrapCommand.Backup"/>: optional CLI override for <c>config.backup.directory</c>. Null means "use the config value (or default)".</param>
/// <param name="EnableAutostart">For <see cref="BootstrapCommand.InstallService"/>: when true, the installer runs <c>systemctl enable --now interfold.service</c> after writing the unit. Defaults to <see cref="BackupSection.AutostartServer"/> on the config.</param>
/// <param name="EnableBackupTimer">For <see cref="BootstrapCommand.InstallService"/>: when true, the installer runs <c>systemctl enable --now interfold-backup.timer</c>. Defaults to <see cref="BackupSection.Enabled"/> on the config.</param>
/// <param name="SystemdUnitDir">For <see cref="BootstrapCommand.InstallService"/>: override the systemd unit installation directory. Defaults to <c>/etc/systemd/system</c>. Tests point this at a temp dir to keep the host's units untouched.</param>
/// <param name="BinaryPathOverride">For <see cref="BootstrapCommand.InstallService"/>: override the installed bootstrapper binary path baked into <c>interfold-backup.service</c>. Defaults to the running binary's location.</param>
/// <param name="AutoRestore">For <see cref="BootstrapCommand.UpdateImages"/>: when true, a failing health check triggers an inline <see cref="BootstrapCommand.Restore"/> against the pre-update backup archives instead of just printing the manual recipe. Defaults to <see cref="UpdateSection.AutoRestoreOnFailure"/> on the config.</param>
/// <param name="SkipPreUpdateBackup">For <see cref="BootstrapCommand.UpdateImages"/>: dangerous escape hatch that skips the pre-update backup. Off by default — a backup ALWAYS happens unless the operator explicitly asks otherwise (e.g. re-running an update after a fresh manual snapshot).</param>
/// <param name="UpdateServices">For <see cref="BootstrapCommand.UpdateImages"/>: CLI override for <see cref="UpdateSection.Services"/>. Empty means "use the config value (which defaults to every service)".</param>
/// <param name="HealthCheckTimeoutOverride">For <see cref="BootstrapCommand.UpdateImages"/>: CLI override for <see cref="UpdateSection.HealthCheckTimeoutSeconds"/>. Null means "use the config value".</param>
/// <param name="RestorePostgresArchive">For <see cref="BootstrapCommand.Restore"/>: path to a specific pg_dump archive to restore. Mutually exclusive with <see cref="RestoreLatest"/> for the postgres component.</param>
/// <param name="RestoreScyllaArchive">For <see cref="BootstrapCommand.Restore"/>: path to a specific scylla .tar.gz archive to restore. Mutually exclusive with <see cref="RestoreLatest"/> for the scylla component.</param>
/// <param name="RestoreLatest">For <see cref="BootstrapCommand.Restore"/>: pick the newest archive by mtime under <c>{backupRoot}/{component}/</c> for every component that wasn't explicitly named on the CLI. Only meaningful when at least one archive exists.</param>
/// <param name="RestoreForce">For <see cref="BootstrapCommand.Restore"/>: skip the interactive "this will wipe your data volumes" confirmation. Required in non-interactive mode; equivalent to typing "y" at the confirmation prompt in interactive mode.</param>
public sealed record BootstrapOptions(
    BootstrapCommand Command,
    string? ConfigPath,
    string OutputDir,
    bool SkipPrereqs,
    bool RotateSecrets,
    bool RotateCerts,
    bool NonInteractive,
    string? FaultInject,
    bool PrintPhaseStatus,
    string BackupComponent = "all",
    int? BackupRetainOverride = null,
    string? BackupDirOverride = null,
    bool EnableAutostart = false,
    bool EnableBackupTimer = false,
    string? SystemdUnitDir = null,
    string? BinaryPathOverride = null,
    bool AutoRestore = false,
    bool SkipPreUpdateBackup = false,
    string[]? UpdateServices = null,
    int? HealthCheckTimeoutOverride = null,
    string? RestorePostgresArchive = null,
    string? RestoreScyllaArchive = null,
    bool RestoreLatest = false,
    bool RestoreForce = false);

public enum BootstrapCommand
{
    Bootstrap,
    Publish,
    Up,
    RotateSecrets,
    RotateCerts,
    /// <summary>
    /// Read-only command: prints the root CA's path, SHA-256 fingerprint, expiry, and the
    /// distribute/verify recipe to stdout. Does NOT regenerate the CA or touch any other
    /// phase — Orchestrator short-circuits before prereqs/config/secrets. Operators run this
    /// to fetch the fingerprint they need to broadcast out-of-band so end-user devices can
    /// verify the cert they download from <c>/.well-known/interfold-root-ca.crt</c>.
    /// </summary>
    ShowTrust,

    /// <summary>
    /// Snapshots the live Postgres + Scylla/Cassandra state to <c>{outputDir}/backups/</c>
    /// (or the operator-supplied directory). Idempotent and short-circuits before any
    /// host-mutating phase (prereqs/config/secrets/certs/publish): only needs an
    /// already-published compose stack + a populated <c>secrets/secrets.json</c>. Driven
    /// either manually by the operator or unattended by the systemd timer installed via
    /// <see cref="InstallService"/>.
    /// </summary>
    Backup,

    /// <summary>
    /// Installs (and optionally enables) the systemd units that own the boot-up autostart
    /// + scheduled-backup wiring. Writes <c>interfold.service</c>,
    /// <c>interfold-backup.service</c>, and <c>interfold-backup.timer</c> to
    /// <c>/etc/systemd/system/</c>, runs <c>systemd-analyze verify</c> on each, then
    /// runs <c>systemctl daemon-reload</c>. The <c>--enable-autostart</c> and
    /// <c>--enable-backup-timer</c> flags additionally <c>systemctl enable --now</c>
    /// the relevant units.
    /// </summary>
    InstallService,

    /// <summary>
    /// Runs a pre-update backup (unless <see cref="BootstrapOptions.SkipPreUpdateBackup"/>),
    /// <c>docker compose pull</c>s new images, <c>up -d</c>s the stack, health-checks
    /// Postgres + Scylla + the API, and either prints the manual restore recipe on
    /// failure or (with <see cref="BootstrapOptions.AutoRestore"/>) invokes
    /// <see cref="Restore"/> inline against the archives captured in the same run.
    /// Idempotent: on a no-image-change run it prunes old backups and exits without
    /// recreating any container.
    /// </summary>
    UpdateImages,

    /// <summary>
    /// Restores the database state from backup archives on disk. Postgres restores
    /// via <c>pg_restore --clean --if-exists</c> against the live compose-exec
    /// endpoint; Scylla restores by stopping the API/web tier, stopping the seed
    /// container, streaming the tar.gz archive back in via <c>docker cp -</c>, and
    /// starting the stack. Destructive — requires explicit
    /// <see cref="BootstrapOptions.RestoreForce"/> (or an interactive "y" prompt) to
    /// proceed.
    /// </summary>
    Restore,
}
