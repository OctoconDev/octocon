using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;
using Interfold.Contracts.Configuration;
using TUnit.Core;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Full-coverage scenarios for the bootstrapper running on ubuntu:24.04. All tests share a single
/// <see cref="UbuntuDinDFixture"/> instance for the lifetime of the test session via TUnit's
/// <c>SharedType.PerTestSession</c>, so the expensive publish / pre-pull happens once.
/// Each test allocates a private scratch directory under <c>/opt/scratch</c> via
/// <see cref="DinDFixtureBase.CreateScratchAsync"/>, allowing the suite to run in parallel
/// without races on <c>/opt/deploy</c>.
/// </summary>
[RequiresDocker]
[ClassDataSource<UbuntuDinDFixture>(Shared = SharedType.PerTestSession)]
public class UbuntuBootstrapTests(UbuntuDinDFixture dinD)
{
    private static string TestConfigJsonPath => Path.Combine(AppContext.BaseDirectory, "fixtures", "interfold.bootstrap.test.json");

    // Trust-store-install tests use a variant that enables /usr/local/share/ca-certificates/
    // writes. That path is shared inside the DinD container across all parallel tests, so the
    // tests that exercise it must also opt in to the NotInParallel guard below.
    private static string TrustInstallConfigJsonPath => Path.Combine(AppContext.BaseDirectory, "fixtures", "interfold.bootstrap.test.trust-install.json");

    [After(Test)]
    public async Task DumpOnFailure(TestContext ctx)
    {
        if (ctx.Execution.Result?.State == TestState.Failed)
        {
            await dinD.CaptureFailureArtifactsAsync(ctx.Metadata.TestName);
        }
        // Tear the per-test compose stack down so the test's dedicated host-port window inside
        // the DinD is released for re-allocation across reruns within the same session. Tests
        // that never called CreateScratchAsync are no-ops here.
        await dinD.TearDownComposeAsync(ctx.Metadata.TestName);
    }

    [Test]
    public async Task ProducesValidComposeOnFreshBox()
    {
        var scratch = await dinD.CreateScratchAsync(nameof(ProducesValidComposeOnFreshBox), TestConfigJsonPath);

        var result = await dinD.RunBootstrapperAsync(nameof(ProducesValidComposeOnFreshBox),
            ["publish", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir, "--non-interactive", "--print-phase-status"]);

        await Assert.That(result.ExitCode).IsEqualTo(0)
            .Because($"bootstrap publish failed: {result.Stderr}");

        var composeBytes = await dinD.CopyOutAsync($"{scratch.OutputDir}/docker-compose.yaml");
        var compose = Encoding.UTF8.GetString(composeBytes);

        await Assert.That(compose).Contains("msg-db:").Because("compose missing msg-db service");
        await Assert.That(compose).Contains("scylla:").Because("compose missing scylla service");
        await Assert.That(compose).Contains("interfold-api").Because("compose missing interfold-api service");
        await Assert.That(compose).DoesNotContain("${Parameters_").Because("unresolved parameter placeholder leaked into compose");

        // Admin work moved into the bootstrapper's DatabaseInitPhase, so the compose graph must
        // no longer reference the legacy bootstrap-auth init containers, and the only
        // *connection string* it ships is the app user's. The msg-db service still gets a
        // POSTGRES_USER / POSTGRES_PASSWORD pair, but those are init-only credentials for the
        // transient `db_init` cluster owner that DatabaseInitPhase scrambles in-cluster as one
        // of its final steps - they're not a connection string anything connects with.
        await Assert.That(compose).DoesNotContain("pg-bootstrap-auth")
            .Because("legacy pg-bootstrap-auth init container leaked back into compose");
        await Assert.That(compose).DoesNotContain("scylla-bootstrap-auth")
            .Because("legacy scylla-bootstrap-auth init container leaked back into compose");
        await Assert.That(compose).DoesNotContain("SCYLLA_ADMIN_PASSWORD")
            .Because("scylla admin password must not appear in compose - it lives in internal.secrets");
        await Assert.That(compose).DoesNotContain("POSTGRES_ADMIN_PASSWORD")
            .Because("postgres admin password must not appear in compose - it lives in internal.secrets");
        await Assert.That(compose).Contains(PostgresRoles.Init)
            .Because("msg-db cluster owner must be the disposable 'db_init' role, not the app user");
        await Assert.That(compose).Contains("POSTGRES_INIT_PASSWORD")
            .Because("compose must reference POSTGRES_INIT_PASSWORD for the disposable cluster owner");
    }

    // Each compose-up test now binds a private host-port window inside the shared DinD via
    // CreateScratchAsync's port allocator, so concurrent `compose up` calls no longer collide on
    // 5000/5432/9042/etc. The previous `ubuntu-compose-up` NotInParallel serialiser is therefore
    // gone — the DinD's inner dockerd throughput is the new (much higher) ceiling.
    [Test]
    public async Task StackComesUpHealthy()
    {
        var scratch = await dinD.CreateScratchAsync(nameof(StackComesUpHealthy), TestConfigJsonPath);

        var result = await dinD.RunBootstrapperAsync(nameof(StackComesUpHealthy),
            ["bootstrap", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir, "--non-interactive", "--skip-prereqs"]);

        await Assert.That(result.ExitCode).IsEqualTo(0)
            .Because($"full bootstrap failed: {result.Stderr}");

        var ps = await dinD.ExecAsync(
            ["docker", "compose", "-f", $"{scratch.OutputDir}/docker-compose.yaml", "ps", "--format", "json"]);
        await Assert.That(ps.ExitCode).IsEqualTo(0L);
        await Assert.That(ps.Stdout).Contains("\"State\":\"running\"").Or.Contains("\"Health\":\"healthy\"")
            .Because("expected at least one healthy/running service in compose ps output");
    }

    [Test]
    public async Task IsIdempotentOnRerun()
    {
        var scratch = await dinD.CreateScratchAsync(nameof(IsIdempotentOnRerun), TestConfigJsonPath);

        await dinD.RunBootstrapperAsync($"{nameof(IsIdempotentOnRerun)}-first",
            ["publish", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir, "--non-interactive", "--print-phase-status"]);

        var firstHash = await ShaOfComposeAsync(scratch);
        var firstSecrets = await dinD.CopyOutAsync($"{scratch.OutputDir}/secrets/secrets.json");

        var second = await dinD.RunBootstrapperAsync($"{nameof(IsIdempotentOnRerun)}-second",
            ["publish", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir, "--non-interactive", "--print-phase-status"]);

        await Assert.That(second.ExitCode).IsEqualTo(0).Because($"second run failed: {second.Stderr}");
        await Assert.That(second.Stderr).Contains("phase=secrets status=skipped")
            .Because("secrets phase should self-skip on a second run");
        await Assert.That(second.Stderr).Contains("phase=certs status=skipped")
            .Because("certs phase should self-skip on a second run");

        var secondHash = await ShaOfComposeAsync(scratch);
        await Assert.That(secondHash).IsEqualTo(firstHash)
            .Because("compose output should be byte-identical across two non-rotating runs");

        var secondSecrets = await dinD.CopyOutAsync($"{scratch.OutputDir}/secrets/secrets.json");
        await Assert.That(Convert.ToHexString(secondSecrets)).IsEqualTo(Convert.ToHexString(firstSecrets))
            .Because("secrets file must be untouched between bootstraps");
    }

    [Test]
    public async Task GeneratedLeafCertHasCorrectSans()
    {
        var scratch = await dinD.CreateScratchAsync(nameof(GeneratedLeafCertHasCorrectSans), TestConfigJsonPath);
        await dinD.RunBootstrapperAsync(nameof(GeneratedLeafCertHasCorrectSans),
            ["publish", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir, "--non-interactive"]);

        var leafBytes = await dinD.CopyOutAsync($"{scratch.OutputDir}/certs/leaf.crt");
        var rootBytes = await dinD.CopyOutAsync($"{scratch.OutputDir}/certs/rootCA.crt");

        using var leaf = X509Certificate2.CreateFromPem(Encoding.UTF8.GetString(leafBytes));
        using var root = X509Certificate2.CreateFromPem(Encoding.UTF8.GetString(rootBytes));

        var sanExt = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        await Assert.That(sanExt).IsNotNull().Because("leaf cert must include a SubjectAlternativeName extension");
        var sans = sanExt!.EnumerateDnsNames().ToList();
        await Assert.That(sans).Contains("api.test.local");
        await Assert.That(sans).Contains("alt.test.local");

        await Assert.That(leaf.Issuer).IsEqualTo(root.Subject)
            .Because("leaf must be signed by the generated root CA");
    }

    [Test]
    public async Task SecretsFileHasRestrictedPermissions()
    {
        var scratch = await dinD.CreateScratchAsync(nameof(SecretsFileHasRestrictedPermissions), TestConfigJsonPath);
        await dinD.RunBootstrapperAsync(nameof(SecretsFileHasRestrictedPermissions),
            ["publish", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir, "--non-interactive"]);

        var stat = await dinD.ExecAsync(["stat", "-c", "%a %U", $"{scratch.OutputDir}/secrets/secrets.json"]);
        await Assert.That(stat.ExitCode).IsEqualTo(0L);
        await Assert.That(stat.Stdout.Trim()).StartsWith("600 ")
            .Because("secrets file must be mode 0600");
        await Assert.That(stat.Stdout.Trim()).EndsWith("root")
            .Because("secrets file must be owned by root");
    }

    // Rotate-secrets runs db-init -> launch (it has to refresh the in-cluster admin password) so
    // it host-binds postgres / scylla / api ports too. Per-test port allocation in
    // CreateScratchAsync means each invocation lands on a private port window, so the previous
    // NotInParallel("ubuntu-compose-up") key is no longer required for safety.
    [Test]
    public async Task RotateSecretsRegeneratesPasswordsAndPreservesCerts()
    {
        var scratch = await dinD.CreateScratchAsync(nameof(RotateSecretsRegeneratesPasswordsAndPreservesCerts), TestConfigJsonPath);
        await dinD.RunBootstrapperAsync($"{nameof(RotateSecretsRegeneratesPasswordsAndPreservesCerts)}-init",
            ["publish", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir, "--non-interactive"]);

        var preSecrets = Encoding.UTF8.GetString(await dinD.CopyOutAsync($"{scratch.OutputDir}/secrets/secrets.json"));
        var preLeafSha = ShaOf(await dinD.CopyOutAsync($"{scratch.OutputDir}/certs/leaf.crt"));

        var rotate = await dinD.RunBootstrapperAsync(nameof(RotateSecretsRegeneratesPasswordsAndPreservesCerts),
            ["rotate-secrets", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir, "--non-interactive"]);
        await Assert.That(rotate.ExitCode).IsEqualTo(0).Because($"rotate-secrets failed: {rotate.Stderr}");

        var postSecrets = Encoding.UTF8.GetString(await dinD.CopyOutAsync($"{scratch.OutputDir}/secrets/secrets.json"));
        var postLeafSha = ShaOf(await dinD.CopyOutAsync($"{scratch.OutputDir}/certs/leaf.crt"));

        await Assert.That(postSecrets).IsNotEqualTo(preSecrets).Because("secrets must rotate");
        await Assert.That(postLeafSha).IsEqualTo(preLeafSha).Because("certs must remain unchanged on rotate-secrets");
    }

    // Rotate-certs also runs db-init (defensive against an empty DB volume) -> launch, so like
    // rotate-secrets it host-binds postgres / scylla / api ports. The per-test port allocation
    // makes that safe to run concurrently with sibling compose-up tests.
    [Test]
    public async Task RotateCertsRegeneratesCertsAndPreservesSecrets()
    {
        var scratch = await dinD.CreateScratchAsync(nameof(RotateCertsRegeneratesCertsAndPreservesSecrets), TestConfigJsonPath);
        await dinD.RunBootstrapperAsync($"{nameof(RotateCertsRegeneratesCertsAndPreservesSecrets)}-init",
            ["publish", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir, "--non-interactive"]);

        var preSecrets = Encoding.UTF8.GetString(await dinD.CopyOutAsync($"{scratch.OutputDir}/secrets/secrets.json"));
        var preLeafSha = ShaOf(await dinD.CopyOutAsync($"{scratch.OutputDir}/certs/leaf.crt"));

        var rotate = await dinD.RunBootstrapperAsync(nameof(RotateCertsRegeneratesCertsAndPreservesSecrets),
            ["rotate-certs", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir, "--non-interactive"]);
        await Assert.That(rotate.ExitCode).IsEqualTo(0).Because($"rotate-certs failed: {rotate.Stderr}");

        var postSecrets = Encoding.UTF8.GetString(await dinD.CopyOutAsync($"{scratch.OutputDir}/secrets/secrets.json"));
        var postLeafSha = ShaOf(await dinD.CopyOutAsync($"{scratch.OutputDir}/certs/leaf.crt"));

        await Assert.That(postSecrets).IsEqualTo(preSecrets).Because("secrets must remain unchanged on rotate-certs");
        await Assert.That(postLeafSha).IsNotEqualTo(preLeafSha).Because("leaf cert must change on rotate-certs");
    }

    [Test]
    public async Task RecoversFromInterruptedPublish()
    {
        var scratch = await dinD.CreateScratchAsync(nameof(RecoversFromInterruptedPublish), TestConfigJsonPath);

        // First invocation halts immediately after the secrets phase via the hidden --fault-inject hook.
        var halted = await dinD.RunBootstrapperAsync($"{nameof(RecoversFromInterruptedPublish)}-halt",
            ["publish", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir, "--non-interactive", "--fault-inject=after-secrets"]);
        await Assert.That(halted.ExitCode).IsEqualTo(0)
            .Because($"fault-inject halt should exit cleanly: {halted.Stderr}");

        // The secrets file should already exist - confirm it.
        var secretsExist = await dinD.ExecAsync(["test", "-f", $"{scratch.OutputDir}/secrets/secrets.json"]);
        await Assert.That(secretsExist.ExitCode).IsEqualTo(0L)
            .Because("partial run should have persisted the secrets file");

        // Re-run from scratch - phases that already ran should skip cleanly.
        var resumed = await dinD.RunBootstrapperAsync($"{nameof(RecoversFromInterruptedPublish)}-resume",
            ["publish", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir, "--non-interactive", "--print-phase-status"]);
        await Assert.That(resumed.ExitCode).IsEqualTo(0)
            .Because($"resumed run should complete cleanly: {resumed.Stderr}");
        await Assert.That(resumed.Stderr).Contains("phase=secrets status=skipped");

        // Compose YAML must be present after the resumed run.
        var composeExists = await dinD.ExecAsync(["test", "-f", $"{scratch.OutputDir}/docker-compose.yaml"]);
        await Assert.That(composeExists.ExitCode).IsEqualTo(0L);
    }

    // Trust-store install writes /usr/local/share/ca-certificates/interfold-root-ca.crt, which is a
    // process-global filesystem path shared across all tests in this DinD. Serialise via NotInParallel
    // so the file copy + `update-ca-certificates` invocation doesn't race with itself.
    [Test]
    [NotInParallel("ubuntu-trust-install")]
    public async Task RootCaInstalledInDebianTrustStore()
    {
        var scratch = await dinD.CreateScratchAsync(nameof(RootCaInstalledInDebianTrustStore), TrustInstallConfigJsonPath);

        var result = await dinD.RunBootstrapperAsync(nameof(RootCaInstalledInDebianTrustStore),
            ["publish", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir, "--non-interactive"]);
        await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);

        // update-ca-certificates places extracted PEMs under /etc/ssl/certs/ symlinked from the anchor.
        var ls = await dinD.ExecAsync(
            ["sh", "-c", "ls /etc/ssl/certs/ | grep -i interfold || true"]);
        await Assert.That(ls.Stdout.Trim()).IsNotEmpty()
            .Because("root CA should have been linked into /etc/ssl/certs after update-ca-certificates");
    }

    private async Task<string> ShaOfComposeAsync(DinDScratch scratch)
    {
        var bytes = await dinD.CopyOutAsync($"{scratch.OutputDir}/docker-compose.yaml");
        return ShaOf(bytes);
    }

    private static string ShaOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
