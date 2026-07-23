using TUnit.Core;
using TUnit.Core.Exceptions;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Guardrail against accidental binary-size regressions. The bootstrapper is published as a
/// self-contained single-file ELF under <c>host/Interfold.Bootstrapper/bin/Release/net10.0/linux-x64/publish</c>;
/// trimming + <c>PublishReadyToRun=false</c> currently keep it well under 75 MiB. If a future
/// change accidentally pulls in a heavy dependency the size will balloon and this test fails
/// before the regression ships.
///
/// Behaviour when the published artifact is absent:
/// <list type="bullet">
///   <item>Locally (CI env var not set): the test is SKIPPED so contributors aren't forced to
///         publish before running unit tests.</item>
///   <item>In CI (<c>CI=true</c>): the test FAILS. The CI workflow is responsible for publishing
///         the bootstrapper before the test step runs — see <c>Dockerfile.bootstrapper</c> and
///         the "Tar.gz binaries + stage linux-x64 for binary-size guardrail" step in
///         <c>.github/workflows/ci-cd.yml</c>. A silent skip in CI would mask a regression in
///         either of those.</item>
/// </list>
/// </summary>
public sealed class BinarySizeGuardrailTests
{
    /// <summary>Hard upper bound. Current binary size sits well below this.</summary>
    private const long MaxBytes = 75L * 1024 * 1024;

    [Test]
    public async Task PublishedLinuxX64BinaryIsBelowThreshold()
    {
        var path = LocatePublishedBinary();
        if (path is null || !File.Exists(path))
        {
            var message =
                $"Published linux-x64 binary not found at expected path '{path ?? "<repo root not located>"}'. " +
                "Run `dotnet publish host/Interfold.Bootstrapper -c Release -r linux-x64 " +
                "/p:PublishProfile=linux-x64` (or build via Dockerfile.bootstrapper) to generate it.";

            if (TestSupport.IsRunningInCi)
            {
                // CI is contractually obligated to publish + stage the binary before this
                // step runs (see ci-cd.yml). Skipping here would let workflow regressions
                // ride into main with a green build, so we fail loudly instead.
                throw new InvalidOperationException(
                    "CI requires the published linux-x64 bootstrapper binary to be present. " + message);
            }

            throw new SkipTestException(message);
        }

        var size = new FileInfo(path).Length;
        await Assert.That(size).IsLessThan(MaxBytes)
            .Because($"binary at {path} is {size / 1024.0 / 1024.0:F1} MiB " +
                     $"(limit {MaxBytes / 1024.0 / 1024.0:F0} MiB)");
    }

    /// <summary>
    /// Walks up to the repo root (identified by Interfold.slnx) and returns the published
    /// binary path under host/Interfold.Bootstrapper/bin/... as specified by the linux-x64
    /// publish profile. The file name is <c>interfold-bootstrap</c> per the assembly name.
    /// </summary>
    private static string? LocatePublishedBinary()
    {
        var repoRoot = TestSupport.LocateRepoRoot();
        if (repoRoot is null) return null;

        return Path.Combine(
            repoRoot,
            "host",
            "Interfold.Bootstrapper",
            "bin",
            "Release",
            "net10.0",
            "linux-x64",
            "publish",
            "interfold-bootstrap");
    }
}
