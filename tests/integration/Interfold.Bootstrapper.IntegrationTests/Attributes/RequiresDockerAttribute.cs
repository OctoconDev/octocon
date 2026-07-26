using System.Diagnostics;

namespace Interfold.Bootstrapper.IntegrationTests.Attributes;

/// <summary>
/// TUnit skip attribute applied at the class level on every DinD-driven test. Probes for a
/// reachable Docker daemon exactly once per test session and short-circuits the entire test
/// class with a "skipped" result if Docker isn't available — devs without Docker get a clean
/// report instead of a fixture-init failure storm.
/// </summary>
/// <remarks>
/// Uses <see cref="Lazy{T}"/> with <c>Task&lt;bool&gt;</c> so the actual <c>docker info</c>
/// process spawn happens at most once even though <see cref="SkipAttribute.ShouldSkip"/> is
/// invoked once per test method. See <see href="https://tunit.dev/docs/writing-tests/skip/#custom-logic"/>.
/// </remarks>
public sealed class RequiresDockerAttribute() : SkipAttribute("Docker daemon not available on the host")
{
    private static readonly Lazy<Task<bool>> DockerAvailable = new(async () =>
    {
        try
        {
            var psi = new ProcessStartInfo("docker", "info")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    });

    public override async Task<bool> ShouldSkip(TestRegisteredContext context)
        => !await DockerAvailable.Value;
}

