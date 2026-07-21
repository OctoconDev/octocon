namespace Interfold.Bootstrapper.IntegrationTests.Fixtures;

/// <summary>
/// DinD fixture rooted on a stripped Fedora 40 image with neither <c>docker</c> nor
/// <c>openssl</c> pre-installed. Used exclusively by <c>PrereqsPhaseTests</c> to exercise the
/// dnf install path inside <c>PrerequisitesPhase</c> (the RedHat-family counterpart of the
/// Ubuntu bare fixture).
/// </summary>
/// <remarks>
/// Shares the fixture-level dockerd-absence guard, <c>PreloadImages = false</c>, and
/// <c>InitializeAsync</c> shape with <see cref="UbuntuBarePrereqsDinDFixture"/> via
/// <see cref="BareDinDFixtureBase"/>.
/// </remarks>
public sealed class FedoraBarePrereqsDinDFixture : BareDinDFixtureBase
{
    protected override string DockerfileName => "Dockerfile.fedora-bare-dind";

    protected override string DistroLabel => "Fedora";

    protected override string PackageManager => "dnf";
}
