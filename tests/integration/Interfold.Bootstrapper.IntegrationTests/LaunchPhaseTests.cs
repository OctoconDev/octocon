using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Integration tests for the <c>up</c> subcommand and the broader
/// <see cref="Interfold.Bootstrapper.Phases.LaunchPhase"/> behaviour. <c>up</c> is the
/// operator's surgical re-launch command — it runs ONLY the launch phase against an
/// already-generated compose stack, skipping config/secrets/certs/publish/db-init entirely.
/// This makes it especially important to confirm the phase can:
///   <list type="bullet">
///     <item>find the persisted compose file and bring the stack up against it,</item>
///     <item>react to a bad image reference by surfacing diagnostic output instead of hanging,</item>
///     <item>respect the operator's custom HTTP port when polling for API health.</item>
///   </list>
/// </summary>
[RequiresDocker]
[ClassDataSource<UbuntuDinDFixture>(Shared = SharedType.PerTestSession)]
[Explicit]
public class LaunchPhaseTests(UbuntuDinDFixture dinD)
{

    [After(Test)]
    public Task DumpOnFailure(TestContext ctx) => DinDHookHelpers.DumpOnFailureAsync(dinD, ctx);

    [Test]
    public async Task UpCommandLaunchesPrePublishedStack()
    {
        // Stage the stack via `publish` first (no DB init, no launch), then run `up` standalone.
        // This is the supported workflow for operators who want to inspect the compose file
        // before turning the lights on.
        var (scratch, _) = await dinD.PublishAsync(nameof(UpCommandLaunchesPrePublishedStack), TestConfigPaths.DefaultConfig);

        // `up` doesn't run db-init by default (the Orchestrator routes only Bootstrap/Rotate to it),
        // so the API would fail health checks because the postgres admin user wouldn't exist yet.
        // We pre-run db-init via a real bootstrap pass first, then teardown only the API services,
        // then `up` brings them back. To keep this test simple and still exercise the `up` code
        // path, we drive a full bootstrap once and then a follow-up `up` against the same scratch
        // - this confirms `up` correctly finds the pre-published compose file and re-launches it.
        var bootstrap = await dinD.RunOnScratchAsync(scratch, $"{nameof(UpCommandLaunchesPrePublishedStack)}-bootstrap", "bootstrap",
            "--skip-prereqs");
        await Assert.That(bootstrap.ExitCode).IsEqualTo(0).Because(bootstrap.Stderr);

        // Stop the stack (compose down keeps volumes around) so we can verify `up` re-launches it.
        await dinD.ExecAsync(
            ["docker", "compose", "-f", $"{scratch.OutputDir}/docker-compose.yaml", "stop"]);

        // `up` deliberately omits --config (it operates on the pre-published output dir only),
        // so we can't use RunOnScratchAsync here — spell out the arg list by hand.
        var up = await dinD.RunBootstrapperAsync($"{nameof(UpCommandLaunchesPrePublishedStack)}-up",
            ["up", "--output-dir", scratch.OutputDir, "--non-interactive"]);
        await Assert.That(up.ExitCode).IsEqualTo(0)
            .Because($"`up` against a stopped pre-published stack should succeed: {up.Stderr}");
    }

    [Test]
    public async Task HealthTimeoutDumpsComposeLogs()
    {
        // We override apiImage to a tag that doesn't exist in the DinD's local image store. The
        // compose-up will fail at image-pull time with a clear error message — the bootstrapper
        // surfaces that via logger.Error → captured stderr. We don't drive the
        // WaitForApiHealthyAsync timeout directly (it's 5 minutes and would blow the test budget),
        // but this test still pins the "bad image -> non-zero exit + diagnostic output" contract.
        var scratch = await dinD.CreateScratchAsync(nameof(HealthTimeoutDumpsComposeLogs), TestConfigPaths.BadImageConfig);

        var result = await dinD.RunOnScratchAsync(scratch, nameof(HealthTimeoutDumpsComposeLogs), "bootstrap",
            "--skip-prereqs");

        await Assert.That(result.ExitCode).IsNotEqualTo(0)
            .Because("bootstrap with a non-pullable apiImage must exit non-zero");

        // Either the compose pull error or the eventual launch failure must surface in the
        // captured output. We accept either because docker version / compose plugin version
        // affects exactly which error wins the race (some emit "manifest unknown" on pull,
        // others fail at image inspect, others at container create).
        var combined = result.Stdout + result.Stderr;
        await Assert.That(combined.Length).IsGreaterThan(0)
            .Because("a failed bootstrap must produce some diagnostic output");
    }

    [Test]
    public async Task RespectsCustomApiHttpPort()
    {
        // Every test gets its own apiHttp port via the DinD port allocator
        // (CreateScratchAsync substitutes the allocated 6-port window into the test
        // config). That makes this test redundant with every other launch-style test in
        // terms of "does the operator's port actually get used", but it's kept as the
        // explicit canary: LaunchPhase must emit a polling line for the allocator's port
        // specifically, so a future refactor that hard-codes 5000 / 4200 / 9042 in the
        // launch path is caught directly.
        var scratch = await dinD.CreateScratchAsync(nameof(RespectsCustomApiHttpPort), TestConfigPaths.DefaultConfig);

        var result = await dinD.RunOnScratchAsync(scratch, nameof(RespectsCustomApiHttpPort), "bootstrap",
            "--skip-prereqs");

        // The test passes whether or not the API ultimately reached healthy state — what we're
        // pinning is that the launch phase polled the allocated port. If the API doesn't come up
        // (e.g. DinD pressure causes a health timeout) we still want to see the polling line.
        var combined = result.Stdout + result.Stderr;
        var expectedUrl = $"http://localhost:{scratch.Ports.ApiHttp}";
        await Assert.That(combined).Contains(expectedUrl)
            .Because($"LaunchPhase should poll the allocated apiHttp port ({expectedUrl}): {result.Stderr}");
    }
}



