namespace Interfold.Bootstrapper.Cli;

/// <summary>
/// The machine-readable reason tokens passed to <see cref="PhaseLogger.PhaseFail"/> /
/// <see cref="PhaseLogger.PhaseSkip"/>. Operator-frozen: they land verbatim in the
/// structured phase log lines that operators grep/alert on, mirroring the role
/// <c>ErrorCodes</c> plays for the HTTP surface.
/// </summary>
internal static class PhaseFailureReasons
{
    // Environment / prerequisites
    public const string NonLinuxHost = "non-linux-host";
    public const string NonRoot = "non-root";
    public const string UnsupportedDistro = "unsupported-distro";

    // Config resolution
    public const string MissingConfig = "missing-config";
    public const string MissingConfigNonInteractive = "missing-config-non-interactive";
    public const string MissingConfigNoTty = "missing-config-no-tty";
    public const string ReconfigureRequiresInteractive = "reconfigure-requires-interactive";

    // Publish / launch
    public const string ComposeNotEmitted = "compose-not-emitted";
    public const string NoComposeFile = "no-compose-file";
    public const string HealthTimeout = "health-timeout";

    // Systemd install
    public const string SystemdAnalyzeVerify = "systemd-analyze-verify";
    public const string InvalidCalendar = "invalid-calendar";
    public const string Systemctl = "systemctl";

    // Backup / restore
    public const string UnknownComponent = "unknown-component";
    public const string MissingSecrets = "missing-secrets";
    public const string InvalidRetain = "invalid-retain";
    public const string MissingAdminPassword = "missing-admin-password";
    public const string EmptyPostgresDump = "empty-postgres-dump";
    public const string NodetoolSnapshot = "nodetool-snapshot";
    public const string ResolveScyllaContainer = "resolve-scylla-container";
    public const string EmptyScyllaArchive = "empty-scylla-archive";
    public const string NoArchives = "no-archives";
    public const string ConfirmationRequired = "confirmation-required";
    public const string StopScylla = "stop-scylla";
    public const string CreateScyllaContainer = "create-scylla-container";
    public const string StartScylla = "start-scylla";

    // Update-images
    public const string PreUpdateBackupFailed = "pre-update-backup-failed";
    public const string PullFailed = "pull-failed";
    public const string UpFailed = "up-failed";
    public const string HealthCheckFailed = "health-check-failed";

    /// <summary>Skip reasons (PhaseSkip) — informational, not failures.</summary>
    public static class Skip
    {
        public const string AlreadyPresent = "already-present";
        public const string OperatorAborted = "operator-aborted";
    }
}
