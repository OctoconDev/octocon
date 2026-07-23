using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;
using TUnit.Core;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Confirms a second <c>bootstrap</c> against an already-running stack short-circuits the
/// expensive admin work via the in-cluster state probes
/// (<c>PostgresAlreadyInitializedAsync</c> / <c>ScyllaAlreadyInitializedAsync</c>) and leaves
/// the existing containers untouched.
/// </summary>
/// <remarks>
/// Both tests in this file run a full bootstrap (which host-binds postgres / scylla / api
/// ports) but each one lands on its own port window inside the shared DinD via
/// <see cref="DinDFixtureBase.CreateScratchAsync"/>'s allocator, so they can execute
/// concurrently with each other and with every other compose-up test in the assembly.
/// </remarks>
[RequiresDocker]
[ClassDataSource<UbuntuDinDFixture>(Shared = SharedType.PerTestSession)]
public class BootstrapIdempotenceTests(UbuntuDinDFixture dinD)
{

    [After(Test)]
    public Task DumpOnFailure(TestContext ctx) => DinDHookHelpers.DumpOnFailureAsync(dinD, ctx);

    [Test]
    public async Task SecondBootstrapShortCircuitsDbInit()
    {
        var scratch = await dinD.CreateScratchAsync(nameof(SecondBootstrapShortCircuitsDbInit), TestConfigPaths.DefaultConfig);

        // First bootstrap: full path - prereqs (skipped) -> config -> secrets -> certs -> publish
        // -> db-init -> launch. Brings the postgres + scylla admin work all the way through.
        var first = await dinD.RunOnScratchAsync(scratch, $"{nameof(SecondBootstrapShortCircuitsDbInit)}-first", "bootstrap",
            "--skip-prereqs");
        await Assert.That(first.ExitCode).IsEqualTo(0)
            .Because($"first bootstrap failed: {first.Stderr}");

        // Second bootstrap against the same scratch: the orchestrator runs the same phase
        // sequence, but each idempotent phase should self-skip or short-circuit.
        var second = await dinD.RunOnScratchAsync(scratch, $"{nameof(SecondBootstrapShortCircuitsDbInit)}-second", "bootstrap",
            "--skip-prereqs", "--print-phase-status");
        await Assert.That(second.ExitCode).IsEqualTo(0)
            .Because($"second bootstrap failed: {second.Stderr}");

        // The machine-readable phase status lines on stderr should report skips for the
        // already-completed work. db-init's short-circuit doesn't emit a `phase=db-init
        // status=skipped` line (it's a fine-grained in-method check), but the stdout from the
        // phase logs the postgres + scylla state-probe success.
        await Assert.That(second.Stderr).Contains("phase=secrets status=skipped")
            .Because("secrets phase should self-skip on a second run");
        await Assert.That(second.Stderr).Contains("phase=certs status=skipped")
            .Because("certs phase should self-skip on a second run");
        await Assert.That(second.Stdout)
            .Contains("postgres already initialised")
            .Because("postgres state probe should report short-circuit on rerun");
        await Assert.That(second.Stdout)
            .Contains("scylla already initialised")
            .Because("scylla state probe should report short-circuit on rerun");
    }

    [Test]
    public async Task SecondBootstrapLeavesContainersHealthy()
    {
        var scratch = await dinD.CreateScratchAsync(nameof(SecondBootstrapLeavesContainersHealthy), TestConfigPaths.DefaultConfig);

        await dinD.RunOnScratchAsync(scratch, $"{nameof(SecondBootstrapLeavesContainersHealthy)}-first", "bootstrap",
            "--skip-prereqs");

        // Capture the container IDs after the first pass so we can compare against the second.
        var composeFile = $"{scratch.OutputDir}/docker-compose.yaml";
        var firstIds = await ContainerIdsAsync(composeFile);
        await Assert.That(firstIds.Count).IsGreaterThan(0)
            .Because("first bootstrap must have brought at least one container up");

        var second = await dinD.RunOnScratchAsync(scratch, $"{nameof(SecondBootstrapLeavesContainersHealthy)}-second", "bootstrap",
            "--skip-prereqs");
        await Assert.That(second.ExitCode).IsEqualTo(0).Because(second.Stderr);

        var secondIds = await ContainerIdsAsync(composeFile);
        // Same set of IDs - if a phase had re-created a container we'd see a different ID for
        // that service. docker compose's stable container naming means an unchanged service has
        // an unchanged container ID across reruns.
        await Assert.That(secondIds.Count).IsEqualTo(firstIds.Count)
            .Because("number of compose services must be stable across reruns");
        foreach (var id in firstIds)
        {
            await Assert.That(secondIds.Contains(id)).IsTrue()
                .Because($"container {id} should still exist after the rerun (no service was recreated)");
        }
    }

    /// <summary>
    /// Returns the set of container IDs for the given compose file. Empty if compose ps fails.
    /// </summary>
    private async Task<HashSet<string>> ContainerIdsAsync(string composeFile)
    {
        var ps = await dinD.ExecAsync(
            ["docker", "compose", "-f", composeFile, "ps", "-q"]);
        if (ps.ExitCode != 0) return new HashSet<string>();
        return ps.Stdout.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }
}



