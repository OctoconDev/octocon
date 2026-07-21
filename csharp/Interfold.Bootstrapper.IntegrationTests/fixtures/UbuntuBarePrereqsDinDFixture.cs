namespace Interfold.Bootstrapper.IntegrationTests.Fixtures;

/// <summary>
/// DinD fixture rooted on a stripped Ubuntu 24.04 image with neither <c>docker</c> nor
/// <c>openssl</c> pre-installed. Used exclusively by <c>PrereqsPhaseTests</c> to exercise the
/// apt install path inside <c>PrerequisitesPhase</c>.
/// </summary>
/// <remarks>
/// <para>
/// Shares fixture-level dockerd-absence guard, <c>PreloadImages = false</c>, and
/// <c>InitializeAsync</c> shape with <see cref="FedoraBarePrereqsDinDFixture"/> via
/// <see cref="BareDinDFixtureBase"/>. See that base class for the full rationale on why
/// the "docker isn't pre-installed" precondition lives at fixture build time rather
/// than inside individual test cases.
/// </para>
/// <para>
/// We share this with <c>PrereqsPhaseTests</c> via <c>SharedType.PerTestSession</c>
/// (like the other DinD fixtures) but each test inside still runs in its own scratch
/// directory so they don't interfere with each other's installed-package state.
/// </para>
/// </remarks>
public sealed class UbuntuBarePrereqsDinDFixture : BareDinDFixtureBase
{
    protected override string DockerfileName => "Dockerfile.ubuntu-bare-dind";

    protected override string DistroLabel => "Ubuntu";

    protected override string PackageManager => "apt";
}
