namespace Interfold.IntegrationTests.Shared.TestServices;

/// <summary>Classifies docker CLI failures caused by a missing/down daemon vs. container
/// runtime errors. Used so preflight helpers report "start Docker Desktop" instead of
/// unrelated remediation (AIO sysctl, memory knobs, etc.).</summary>
public static class DockerDaemonAvailability
{
    public static bool LooksUnavailable(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.Contains("failed to connect to the docker API", StringComparison.OrdinalIgnoreCase)
            || text.Contains("dockerDesktopLinuxEngine", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Is the docker daemon running", StringComparison.OrdinalIgnoreCase)
            || text.Contains("error during connect", StringComparison.OrdinalIgnoreCase)
            || text.Contains("npipe:////./pipe/", StringComparison.OrdinalIgnoreCase)
            || (text.Contains("unix:///var/run/docker.sock", StringComparison.OrdinalIgnoreCase)
                && text.Contains("connect", StringComparison.OrdinalIgnoreCase));
    }
}
