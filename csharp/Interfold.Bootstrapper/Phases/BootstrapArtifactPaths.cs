namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// The on-disk artifact conventions every phase shares, replacing per-phase copies of the
/// compose-file discovery helper and the config-path fallback. The file names are frozen —
/// operators, systemd units, and existing deployments reference them.
/// </summary>
internal static class BootstrapArtifactPaths
{
    public const string ComposeFileName = "docker-compose.yaml";
    public const string BootstrapConfigFileName = "interfold.bootstrap.json";
    public const string SecretsRelativePath = "secrets/secrets.json";

    /// <summary>
    /// Finds the emitted compose file under <paramref name="outputDir"/>: the conventional
    /// top-level path first, then a recursive search (Aspire >= 13 sometimes emits into a
    /// subdirectory keyed by the environment name). Null when absent.
    /// </summary>
    public static string? FindComposeFile(string outputDir)
    {
        var direct = Path.Combine(outputDir, ComposeFileName);
        if (File.Exists(direct)) return direct;
        return Directory.EnumerateFiles(outputDir, ComposeFileName, SearchOption.AllDirectories).FirstOrDefault();
    }

    /// <summary>
    /// Like <see cref="FindComposeFile"/> but never null: falls back to the conventional
    /// full path even when the file doesn't exist yet (install-service pre-populates unit
    /// files for image-baking workflows; compose-up surfaces the missing file later).
    /// </summary>
    public static string ResolveComposeFileOrConventional(string outputDir)
    {
        var found = FindComposeFile(outputDir);
        return Path.GetFullPath(found ?? Path.Combine(outputDir, ComposeFileName));
    }

    /// <summary>The bootstrap config path: explicit <c>--config</c> when supplied, else
    /// <c>{outputDir}/interfold.bootstrap.json</c>.</summary>
    public static string ResolveConfigPath(Cli.BootstrapOptions options)
        => options.ConfigPath ?? Path.Combine(options.OutputDir, BootstrapConfigFileName);
}
