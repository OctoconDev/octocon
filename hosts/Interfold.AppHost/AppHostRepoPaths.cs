namespace Interfold.AppHost;

/// <summary>Repo-root-relative paths the AppHost graph bind-mounts. Absolute paths are
/// required — relative <c>../../db/...</c> breaks when the AppHost cwd is the repo root
/// or a test bin directory.</summary>
public static class AppHostRepoPaths
{
    public static string ScyllaRackDcProperties(string repoRoot, string regionWire) =>
        Path.GetFullPath(Path.Combine(repoRoot, "db", "scylla", $"cassandra-rackdc.{regionWire}.properties"));

    /// <summary>Walks up from <see cref="AppContext.BaseDirectory"/> until it finds
    /// <c>Interfold.slnx</c>.</summary>
    public static string ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Interfold.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate 'Interfold.slnx' walking up from '{AppContext.BaseDirectory}'. " +
            "The AppHost needs the repo root for Scylla rackdc bind mounts.");
    }
}
