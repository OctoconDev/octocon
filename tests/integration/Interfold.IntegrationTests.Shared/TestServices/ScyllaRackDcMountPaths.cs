namespace Interfold.IntegrationTests.Shared.TestServices;

/// <summary>Absolute rackdc bind-mount sources for Scylla. Path shape matches
/// <c>Interfold.AppHost.AppHostRepoPaths.ScyllaRackDcProperties</c> — keep in sync.</summary>
public static class ScyllaRackDcMountPaths
{
    public static string RackDcFile(string repoRoot, string regionWire) =>
        Path.GetFullPath(Path.Combine(repoRoot, "db", "scylla", $"cassandra-rackdc.{regionWire}.properties"));

    /// <summary>Verifies every regional rackdc file the multi-DC graph can bind exists on disk.
    /// Throws with an actionable message when a mount source is missing.</summary>
    public static void VerifyAllPresent(string repoRoot)
    {
        var missing = new List<string>();
        foreach (var region in new[] { "nam", "eur", "sam", "sas", "eas", "ocn", "gdpr" })
        {
            var path = RackDcFile(repoRoot, region);
            if (!File.Exists(path))
                missing.Add(path);
        }

        if (missing.Count == 0)
            return;

        throw new InvalidOperationException(
            "Scylla GossipingPropertyFileSnitch requires rackdc bind-mount sources under db/scylla/, " +
            "but the following files are missing:\n  " +
            string.Join("\n  ", missing) +
            "\nMulti-DC topology depends on these files — do not fall back to SimpleSnitch.");
    }
}
