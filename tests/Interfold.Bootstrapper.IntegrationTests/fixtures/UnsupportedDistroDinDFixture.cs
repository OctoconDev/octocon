namespace Interfold.Bootstrapper.IntegrationTests.Fixtures;

/// <summary>
/// Negative-path fixture: a glibc-compatible Debian-slim base whose <c>/etc/os-release</c>
/// claims an unknown distro ID. The bootstrapper binary execs successfully, hits
/// <c>PrerequisitesPhase</c>, and refuses with the "Unsupported Linux distribution" message
/// before touching any package manager. See <c>Dockerfile.unsupported-distro-dind</c>.
/// </summary>
public sealed class UnsupportedDistroDinDFixture : DinDFixtureBase
{
    protected override string DockerfileName => "Dockerfile.unsupported-distro-dind";

    // No Docker daemon to wait for, no API image to load, no images to pre-pull.
    protected override bool PreloadImages => false;
}

