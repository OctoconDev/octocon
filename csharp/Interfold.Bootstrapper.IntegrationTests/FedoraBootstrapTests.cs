using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;
using TUnit.Core;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// RHEL/Fedora-side smoke coverage. Scenarios overlap with the Ubuntu suite in spirit but
/// specifically exercise the <c>dnf</c> install path of <c>PrerequisitesPhase</c> and the
/// <c>update-ca-trust extract</c> trust-store install in <c>CertificatePhase</c>.
/// Each test allocates its own scratch dir so the suite can execute concurrently.
/// </summary>
[RequiresDocker]
[ClassDataSource<FedoraDinDFixture>(Shared = SharedType.PerTestSession)]
public class FedoraBootstrapTests(FedoraDinDFixture dinD)
{

    // Trust-store-install tests use a variant that enables /etc/pki/ca-trust/source/anchors/
    // writes. See Ubuntu suite for the parallel-safety rationale.

    [After(Test)]
    public Task DumpOnFailure(TestContext ctx) => DinDHookHelpers.DumpOnFailureAsync(dinD, ctx);

    // Each compose-up test draws a unique host-port window from the DinD-wide port allocator
    // (see DinDFixtureBase.CreateScratchAsync), so the previous fedora-compose-up serialiser
    // is no longer required to avoid host-port collisions.
    [Test]
    public async Task StackComesUpHealthyOnRhelFamily()
    {
        var (scratch, _) = await dinD.BootstrapAsync(nameof(StackComesUpHealthyOnRhelFamily), TestConfigPaths.DefaultConfig);

        var ps = await dinD.ExecAsync(
            ["docker", "compose", "-f", $"{scratch.OutputDir}/docker-compose.yaml", "ps", "--format", "json"]);
        await Assert.That(ps.ExitCode).IsEqualTo(0L);
        await Assert.That(ps.Stdout).Contains("\"State\":\"running\"").Or.Contains("\"Health\":\"healthy\"")
            .Because("expected at least one healthy/running service in Fedora compose ps output");
    }

    // Trust-store install writes /etc/pki/ca-trust/source/anchors/interfold-root-ca.crt, a
    // process-global path shared across tests in this DinD - serialise on a per-fixture key.
    [Test]
    [NotInParallel("fedora-trust-install")]
    public async Task RootCaInstalledInRhelTrustStore()
    {
        var (scratch, _) = await dinD.PublishAsync(nameof(RootCaInstalledInRhelTrustStore), TestConfigPaths.TrustInstallConfig);

        // update-ca-trust extract writes the extracted PEM bundles to /etc/pki/ca-trust/extracted/.
        var lookup = await dinD.ExecAsync(
            ["sh", "-c",
             "grep -l 'Interfold Test Root CA' /etc/pki/ca-trust/extracted/pem/tls-ca-bundle.pem || true"]);
        await Assert.That(lookup.Stdout.Trim()).IsNotEmpty()
            .Because("root CA subject should appear in /etc/pki/ca-trust/extracted/pem/tls-ca-bundle.pem after update-ca-trust extract");
    }
}



