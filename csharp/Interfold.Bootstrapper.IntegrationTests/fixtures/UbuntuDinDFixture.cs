namespace Interfold.Bootstrapper.IntegrationTests.Fixtures;

/// <summary>
/// DinD fixture rooted on ubuntu:24.04 — exercises the apt path of <c>PrerequisitesPhase</c>
/// and the Debian/Ubuntu trust-store install in <c>CertificatePhase</c>.
/// </summary>
public sealed class UbuntuDinDFixture : DinDFixtureBase
{
    protected override string DockerfileName => "Dockerfile.ubuntu-dind";
}

