namespace Interfold.Bootstrapper.Cli;

/// <summary>
/// Log-name mapping for the standalone CLI commands (the ones that are not orchestrated
/// bootstrap phases and therefore deliberately absent from <c>BootstrapPhase</c>). The
/// strings are frozen — they prefix every structured log line the command emits.
/// </summary>
internal static class BootstrapCommandExtensions
{
    public static string ToPhaseLogName(this BootstrapCommand command) => command switch
    {
        BootstrapCommand.Backup => "backup",
        BootstrapCommand.Restore => "restore",
        BootstrapCommand.UpdateImages => "update-images",
        BootstrapCommand.InstallService => "install-service",
        _ => throw new ArgumentOutOfRangeException(
            nameof(command), command, "Only the standalone CLI commands carry a phase log name."),
    };
}
