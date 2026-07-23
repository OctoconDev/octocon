namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// The database component targeted by <c>backup</c>/<c>restore</c>. CLI wire values
/// (<c>postgres</c> / <c>scylla</c> / <c>all</c>) are frozen — operators and the systemd
/// backup timer pass them verbatim.
/// </summary>
internal enum BackupDatabaseComponent
{
    Postgres,
    Scylla,
    All,
}

internal static class BackupDatabaseComponentExtensions
{
    public static string ToWireValue(this BackupDatabaseComponent component) => component switch
    {
        BackupDatabaseComponent.Postgres => "postgres",
        BackupDatabaseComponent.Scylla => "scylla",
        BackupDatabaseComponent.All => "all",
        _ => throw new ArgumentOutOfRangeException(nameof(component), component, "Unhandled BackupDatabaseComponent."),
    };

    /// <summary>Case-insensitive parse; null/empty resolves to <see cref="BackupDatabaseComponent.All"/>
    /// (the CLI default), unknown values return null so the caller can emit its legacy error.</summary>
    public static BackupDatabaseComponent? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return BackupDatabaseComponent.All;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "postgres" => BackupDatabaseComponent.Postgres,
            "scylla" => BackupDatabaseComponent.Scylla,
            "all" => BackupDatabaseComponent.All,
            _ => null,
        };
    }
}

/// <summary>
/// On-disk layout constants for the backup root. The subdirectory names and archive
/// extensions are frozen — restore/update flows and existing operator backups rely on them.
/// </summary>
internal static class BackupStoragePaths
{
    public const string PostgresDir = "postgres";
    public const string ScyllaDir = "scylla";
    public const string PostgresArchivePattern = "*.dump";
    public const string ScyllaArchivePattern = "*.tar.gz";

    /// <summary>
    /// Returns the newest file matching <paramref name="pattern"/> under
    /// <paramref name="dir"/> by last-write time, or <c>null</c> if the directory
    /// doesn't exist or contains no matching files.
    /// </summary>
    public static FileInfo? LatestFile(string dir, string pattern)
    {
        if (!Directory.Exists(dir)) return null;
        return new DirectoryInfo(dir)
            .EnumerateFiles(pattern, SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
    }
}
