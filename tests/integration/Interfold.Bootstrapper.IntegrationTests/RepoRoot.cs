namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Resolves the repository root by walking up from the test binary's directory looking for the
/// solution file. Avoids hardcoded relative paths that break when the test runner copies binaries
/// to a non-standard location.
/// </summary>
internal static class RepoRoot
{
    private static readonly Lazy<string> Resolved = new(Resolve);

    public static string Path => Resolved.Value;

    public static string Combine(params string[] segments) => System.IO.Path.Combine([Path, .. segments]);

    private static string Resolve()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("Interfold.slnx").Length > 0)
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"Could not locate Interfold.slnx starting from {AppContext.BaseDirectory}.");
    }
}

