using System.Reflection;
using System.Text;
using Interfold.Bootstrapper.Cli;

namespace Interfold.Bootstrapper.Util;

/// <summary>
/// Embedded <c>support/</c> resources (Scylla rackdc, nginx template, Cassandra Dockerfile,
/// ensure-host-aio.sh). Read via <see cref="Open"/>; materialize to disk only when Docker
/// needs a host bind-mount path.
/// </summary>
internal static class EmbeddedSupportFiles
{
    internal const string ResourcePrefix = "support/";

    private static Assembly BootstrapperAssembly => typeof(EmbeddedSupportFiles).Assembly;

    internal const string NginxTemplateRelative = "web/nginx/default.conf.template";
    internal const string CassandraDockerfileRelative = "db/cassandra/Dockerfile";

    internal static string RackDcRelative(string regionWire) =>
        $"db/scylla/cassandra-rackdc.{regionWire}.properties";

    internal static string SupportRoot(string outputDir) =>
        Path.GetFullPath(Path.Combine(outputDir, "support"));

    internal static string SupportFilePath(string outputDir, string relativeSupportPath)
    {
        var supportRoot = SupportRoot(outputDir);
        var targetPath = Path.GetFullPath(Path.Combine(
            supportRoot,
            relativeSupportPath.Replace('/', Path.DirectorySeparatorChar)));

        if (!IsUnderBase(targetPath, supportRoot))
        {
            throw new InvalidOperationException(
                $"Support path '{relativeSupportPath}' resolves to '{targetPath}', " +
                $"which is outside support root '{supportRoot}'.");
        }

        return targetPath;
    }

    /// <summary>Opens an embedded support resource as a read stream.</summary>
    internal static Stream Open(string relativeSupportPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeSupportPath);
        var resourceName = ToResourceName(relativeSupportPath);
        return BootstrapperAssembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded support resource '{resourceName}' was not found.");
    }

    /// <summary>
    /// Writes <paramref name="relativeSupportPath"/> to <paramref name="targetPath"/> when
    /// missing. Returns <c>true</c> when a new file was written; <c>false</c> when skipped
    /// (already exists or lost a concurrent create race).
    /// </summary>
    internal static bool Materialize(string relativeSupportPath, string targetPath, PhaseLogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeSupportPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        if (File.Exists(targetPath))
            return false;

        var resourceName = ToResourceName(relativeSupportPath);
        try
        {
            WriteResourceToPath(resourceName, targetPath);
            return true;
        }
        catch (IOException)
        {
            // Lost a concurrent Materialize race between File.Exists and rename — equivalent bytes.
            return false;
        }
    }

    /// <summary>Materializes under <see cref="SupportRoot"/>; returns the absolute host path.</summary>
    internal static string MaterializeUnderSupportRoot(
        string outputDir, string relativeSupportPath, PhaseLogger? logger = null)
    {
        var targetPath = SupportFilePath(outputDir, relativeSupportPath);
        Materialize(relativeSupportPath, targetPath, logger);
        return targetPath;
    }

    internal static IReadOnlyList<string> EnumerateSupportResourceNames() =>
        BootstrapperAssembly
            .GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .ToList();

    internal static string ReadAllText(string relativeSupportPath)
    {
        using var stream = Open(relativeSupportPath);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string ToResourceName(string relativeSupportPath) =>
        ResourcePrefix + relativeSupportPath.Replace('\\', '/').TrimStart('/');

    private static bool IsUnderBase(string candidate, string canonicalBase)
    {
        var withSep = canonicalBase.EndsWith(Path.DirectorySeparatorChar)
            ? canonicalBase
            : canonicalBase + Path.DirectorySeparatorChar;
        return candidate.StartsWith(withSep, StringComparison.Ordinal) || candidate == canonicalBase;
    }

    private static void WriteResourceToPath(string resourceName, string targetPath)
    {
        using var stream = BootstrapperAssembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Manifest resource '{resourceName}' enumerated but GetManifestResourceStream returned null.");

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        var tempPath = $"{targetPath}.{Environment.ProcessId}.tmp";
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.CopyTo(fs);
            }

            File.Move(tempPath, targetPath, overwrite: false);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
            throw;
        }

        MaybeSetExecutableBit(targetPath);
    }

    private static void MaybeSetExecutableBit(string targetPath)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        if (!targetPath.EndsWith(".sh", StringComparison.Ordinal))
            return;

        var current = File.GetUnixFileMode(targetPath);
        File.SetUnixFileMode(targetPath,
            current
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherExecute);
    }
}
