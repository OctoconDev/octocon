using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;
using TUnit.Core;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Confirms the <c>trustStoreInstall=false</c> branch of <c>CertificatePhase</c> really does
/// leave the system trust store untouched. The default test fixture config sets the flag to
/// false; this test runs <c>publish</c> with that config and asserts the Debian anchor
/// directory (<c>/usr/local/share/ca-certificates/</c>) has no <c>interfold-root-ca*</c>
/// symlinks afterwards.
/// </summary>
/// <remarks>
/// The complementary positive case (<c>trustStoreInstall=true</c> → root CA appears under
/// <c>/etc/ssl/certs/</c>) is already covered by
/// <see cref="UbuntuBootstrapTests.RootCaInstalledInDebianTrustStore"/>.
/// </remarks>
[RequiresDocker]
[ClassDataSource<UbuntuDinDFixture>(Shared = SharedType.PerTestSession)]
public class TrustStoreFalseTests(UbuntuDinDFixture dinD)
{

    [After(Test)]
    public Task DumpOnFailure(TestContext ctx) => DinDHookHelpers.DumpOnFailureAsync(dinD, ctx);

    // Shares the "ubuntu-trust-install" NotInParallel key with
    // UbuntuBootstrapTests.RootCaInstalledInDebianTrustStore (the positive case) so we never run
    // a `trustStoreInstall=true` publish concurrently with this `trustStoreInstall=false`
    // publish against the same DinD. Without the key the two could race on
    // /usr/local/share/ca-certificates and either fight over update-ca-certificates or
    // false-flag this test when the install test landed the cert first.
    [Test]
    [NotInParallel("ubuntu-trust-install")]
    public async Task TrustStoreInstallFalseLeavesSystemStoreClean()
    {
        // Snapshot the trust-store paths BEFORE running publish. The DinD fixture is shared
        // (SharedType.PerTestSession) with UbuntuBootstrapTests.RootCaInstalledInDebianTrustStore,
        // so a successful sibling run can leave `interfold-root-ca.crt` in the anchor dir and
        // its hashed symlinks under /etc/ssl/certs. The real assertion we want here is
        // "publish with trustStoreInstall=false didn't ADD anything", so we compare pre vs post
        // listings instead of demanding a clean global state.
        var anchorBefore = await ListInterfoldEntriesAsync("/usr/local/share/ca-certificates/");
        var sslCertsBefore = await ListInterfoldEntriesAsync("/etc/ssl/certs/");

        var (scratch, _) = await dinD.PublishAsync(nameof(TrustStoreInstallFalseLeavesSystemStoreClean), TestConfigPaths.DefaultConfig);

        var anchorAfter = await ListInterfoldEntriesAsync("/usr/local/share/ca-certificates/");
        var sslCertsAfter = await ListInterfoldEntriesAsync("/etc/ssl/certs/");

        var anchorAdded = anchorAfter.Except(anchorBefore).ToArray();
        await Assert.That(anchorAdded).IsEmpty()
            .Because($"trustStoreInstall=false should not write new interfold-* entries to the anchor directory; pre={string.Join(',', anchorBefore)} post={string.Join(',', anchorAfter)}");

        var sslCertsAdded = sslCertsAfter.Except(sslCertsBefore).ToArray();
        await Assert.That(sslCertsAdded).IsEmpty()
            .Because($"trustStoreInstall=false should not write new interfold-* entries to /etc/ssl/certs; pre={string.Join(',', sslCertsBefore)} post={string.Join(',', sslCertsAfter)}");

        // Sanity: the generated cert files themselves should still exist under the scratch dir —
        // it's only the SYSTEM trust install that should be skipped.
        var leafExists = await dinD.ExecAsync(["test", "-f", $"{scratch.OutputDir}/certs/leaf.crt"]);
        await Assert.That(leafExists.ExitCode).IsEqualTo(0L)
            .Because("the leaf cert artifact must still be produced even when trust-store install is off");
    }

    private async Task<HashSet<string>> ListInterfoldEntriesAsync(string directory)
    {
        var ls = await dinD.ExecAsync(
            ["sh", "-c", $"ls {directory} 2>/dev/null | grep -i interfold || true"]);
        return ls.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }
}


