namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// The systemd unit names the bootstrapper installs. Frozen operator contract —
/// `systemctl status interfold` muscle memory, existing installs, and docs all reference
/// them. The template files under <c>Phases/SystemdTemplates/</c> carry the same names
/// manually (embedded resources can't reference constants); keep them in sync when renaming.
/// </summary>
internal static class SystemdUnitNames
{
    public const string Interfold = "interfold.service";
    public const string Backup = "interfold-backup.service";
    public const string BackupTimer = "interfold-backup.timer";
    public const string Update = "interfold-update.service";
}
