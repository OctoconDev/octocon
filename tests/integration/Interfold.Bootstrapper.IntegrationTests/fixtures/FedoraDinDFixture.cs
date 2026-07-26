namespace Interfold.Bootstrapper.IntegrationTests.Fixtures;

/// <summary>
/// DinD fixture rooted on fedora:40 — exercises the dnf path of <c>PrerequisitesPhase</c>
/// and the RHEL/Fedora trust-store install via <c>update-ca-trust extract</c>.
/// </summary>
public sealed class FedoraDinDFixture : DinDFixtureBase
{
    protected override string DockerfileName => "Dockerfile.fedora-dind";
}

