using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Exercises <see cref="Interfold.Bootstrapper.Phases.PrerequisitesPhase"/> against
/// "bare" Linux fixtures where neither <c>docker</c> nor <c>openssl</c> is pre-installed.
/// </summary>
/// <remarks>
/// <para>
/// We drive the phase via <c>bootstrap --fault-inject=after-prereqs</c>: the fault hook halts
/// the orchestrator cleanly after the prereqs phase returns, so we never enter <c>config</c>
/// or any later phase. That keeps the test surface tight (we only assert on what
/// <c>PrerequisitesPhase</c> mutated) and avoids needing a working dockerd inside the bare
/// fixture.
/// </para>
/// <para>
/// These tests share state inside the same DinD container (installed packages,
/// /etc/sysctl.d entries). That's OK: each test asserts on the end-state, and
/// reinstall-via-apt/dnf is a no-op once the package is already present. We serialise them
/// under <c>ubuntu-bare-prereqs</c> / <c>fedora-bare-prereqs</c> just to avoid running
/// concurrent apt/dnf operations in the same container.
/// </para>
/// </remarks>
[RequiresDocker]
[ClassDataSource<UbuntuBarePrereqsDinDFixture>(Shared = SharedType.PerTestSession)]
public class UbuntuPrereqsPhaseTests(UbuntuBarePrereqsDinDFixture dinD)
{
    [After(Test)]
    public Task DumpOnFailure(TestContext ctx) => DinDHookHelpers.DumpOnFailureAsync(dinD, ctx, teardown: false);

    [Test]
    [NotInParallel("ubuntu-bare-prereqs")]
    public async Task InstallsDockerOnDebianWhenAbsent()
    {
        // The "docker isn't pre-installed" pre-condition has been lifted to
        // UbuntuBarePrereqsDinDFixture.InitializeAsync where it runs exactly once at fixture
        // build time. Inside the shared session-scoped fixture the apt install is idempotent —
        // re-running the bootstrap path on an already-bootstrapped container should still leave
        // docker on PATH, which is what this test verifies. The post-condition below therefore
        // holds regardless of whether this test runs first or after a sibling test in the same
        // [NotInParallel("ubuntu-bare-prereqs")] group has already triggered the install.
        var result = await dinD.RunBootstrapperAsync(nameof(InstallsDockerOnDebianWhenAbsent),
            ["bootstrap", "--non-interactive", "--fault-inject=after-prereqs"]);
        await Assert.That(result.ExitCode).IsEqualTo(0L)
            .Because($"bootstrap should halt cleanly after prereqs; got stderr: {result.Stderr}");

        var postPath = await dinD.ExecAsync(["sh", "-c", "command -v docker"]);
        await Assert.That(postPath.ExitCode).IsEqualTo(0L)
            .Because($"docker must be on PATH after PrerequisitesPhase; stdout={postPath.Stdout}, stderr={postPath.Stderr}");
    }

    [Test]
    [NotInParallel("ubuntu-bare-prereqs")]
    public async Task InstallsOpenSslWhenAbsent()
    {
        // openssl may be left over from a previous test in this fixture's lifetime, so we
        // only assert on the post-state. The relevant codepath in PrereqsPhase is idempotent.
        var result = await dinD.RunBootstrapperAsync(nameof(InstallsOpenSslWhenAbsent),
            ["bootstrap", "--non-interactive", "--fault-inject=after-prereqs"]);
        await Assert.That(result.ExitCode).IsEqualTo(0L).Because(result.Stderr);

        var postPath = await dinD.ExecAsync(["sh", "-c", "command -v openssl"]);
        await Assert.That(postPath.ExitCode).IsEqualTo(0L)
            .Because($"openssl must be on PATH after PrerequisitesPhase; stdout={postPath.Stdout}, stderr={postPath.Stderr}");
    }

    [Test]
    [NotInParallel("ubuntu-bare-prereqs")]
    public async Task PersistsAioSysctlDropIn()
    {
        var result = await dinD.RunBootstrapperAsync(nameof(PersistsAioSysctlDropIn),
            ["bootstrap", "--non-interactive", "--fault-inject=after-prereqs"]);
        await Assert.That(result.ExitCode).IsEqualTo(0L).Because(result.Stderr);

        // PrereqsPhase writes /etc/sysctl.d/99-interfold.conf with a topology-sized
        // `fs.aio-max-nr = <N>`. With no config file (the --non-interactive path used here),
        // the AIO sizing falls back to the single-node baseline (1 * 116562 + 50000 = 166562);
        // we assert only on the key being present so the test stays robust if the per-node
        // constants change.
        var content = await dinD.ExecAsync(["sh", "-c", "cat /etc/sysctl.d/99-interfold.conf || true"]);
        await Assert.That(content.Stdout).Contains("fs.aio-max-nr")
            .Because($"sysctl drop-in should contain fs.aio-max-nr; got: {content.Stdout}");
    }

    [Test]
    [NotInParallel("ubuntu-bare-prereqs")]
    public async Task RefusesWhenNotRoot()
    {
        // The bare ubuntu Dockerfile creates `testuser` (uid 4242). Run the bootstrapper as that
        // user; PrerequisitesPhase.EnsureRoot must reject the invocation with a clear message.
        // We use `su -c` to switch identity. The bootstrapper prints to stderr and exits
        // non-zero, so the exit propagates back through su.
        var result = await dinD.ExecAsync(
            ["su", "testuser", "-c",
             $"{DinDFixtureBase.BootstrapperMountPath}/interfold-bootstrap bootstrap --non-interactive --fault-inject=after-prereqs"]);

        await Assert.That(result.ExitCode).IsNotEqualTo(0L)
            .Because("non-root invocation must exit non-zero");

        var combined = result.Stdout + result.Stderr;
        await Assert.That(combined).Contains("with sudo")
            .Or.Contains("non-root")
            .Or.Contains("require root")
            .Because($"bootstrapper should explain the non-root rejection; got: {combined}");
    }
}

/// <summary>
/// Fedora-family analog of <see cref="UbuntuPrereqsPhaseTests"/>. Exercises the dnf install
/// path inside <see cref="Interfold.Bootstrapper.Phases.PrerequisitesPhase"/>.
/// </summary>
[RequiresDocker]
[ClassDataSource<FedoraBarePrereqsDinDFixture>(Shared = SharedType.PerTestSession)]
public class FedoraPrereqsPhaseTests(FedoraBarePrereqsDinDFixture dinD)
{
    [After(Test)]
    public Task DumpOnFailure(TestContext ctx) => DinDHookHelpers.DumpOnFailureAsync(dinD, ctx, teardown: false);

    [Test]
    [NotInParallel("fedora-bare-prereqs")]
    public async Task InstallsDockerOnFedoraWhenAbsent()
    {
        // Mirrors InstallsDockerOnDebianWhenAbsent: the "bare fixture has no docker" guard
        // now lives in FedoraBarePrereqsDinDFixture.InitializeAsync and runs once at fixture
        // build time. dnf install is idempotent so the post-condition holds regardless of
        // sibling-test ordering inside the [NotInParallel("fedora-bare-prereqs")] group.
        var result = await dinD.RunBootstrapperAsync(nameof(InstallsDockerOnFedoraWhenAbsent),
            ["bootstrap", "--non-interactive", "--fault-inject=after-prereqs"]);
        await Assert.That(result.ExitCode).IsEqualTo(0L).Because(result.Stderr);

        var postPath = await dinD.ExecAsync(["sh", "-c", "command -v docker"]);
        await Assert.That(postPath.ExitCode).IsEqualTo(0L)
            .Because($"docker must be on PATH after PrerequisitesPhase on Fedora; stdout={postPath.Stdout}, stderr={postPath.Stderr}");
    }
}


