using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Negative-path coverage: when the bootstrapper runs on a distro family it does not support,
/// it must exit non-zero with a clear "unsupported distro" message and must not have made
/// partial filesystem changes under its output directory. The fixture is a glibc Debian-slim base
/// with a doctored <c>/etc/os-release</c> so the binary actually execs before being rejected.
/// </summary>
[RequiresDocker]
[ClassDataSource<UnsupportedDistroDinDFixture>(Shared = SharedType.PerTestSession)]
public class UnsupportedOsTests(UnsupportedDistroDinDFixture dinD)
{

    [After(Test)]
    public Task DumpOnFailure(TestContext ctx) => DinDHookHelpers.DumpOnFailureAsync(dinD, ctx, teardown: false);

    [Test]
    public async Task RefusesToRunOnUnsupportedDistro()
    {
        var scratch = await dinD.CreateScratchAsync(nameof(RefusesToRunOnUnsupportedDistro), TestConfigPaths.DefaultConfig);

        var result = await dinD.RunOnScratchAsync(scratch, nameof(RefusesToRunOnUnsupportedDistro), "bootstrap");

        await Assert.That(result.ExitCode).IsNotEqualTo(0L)
            .Because("bootstrap must exit non-zero on an unsupported distro");

        var combined = result.Stdout + result.Stderr;
        await Assert.That(combined).Contains("Unsupported Linux distribution").Or.Contains("unsupported-distro")
            .Because("error message should explicitly identify the unsupported distro");

        // Filesystem state must be untouched - secrets/certs/compose should not exist.
        // wc -l returns the number of lines from `ls`, which is 0 when none of the paths
        // exist. Assert on the parsed count, not the ExecResult itself.
        var any = await dinD.ExecAsync(
            ["sh", "-c", $"ls {scratch.OutputDir}/secrets {scratch.OutputDir}/certs {scratch.OutputDir}/docker-compose.yaml 2>/dev/null | wc -l"]);
        await Assert.That(int.Parse(any.Stdout.Trim())).IsEqualTo(0)
            .Because("a refused run must not leave partial artifacts under the output dir");
    }
}



