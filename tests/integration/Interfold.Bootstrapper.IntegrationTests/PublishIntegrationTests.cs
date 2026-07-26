using System.Text;
using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;
using Interfold.Bootstrapper.IntegrationTests.TestServices;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// Integration tests for <see cref="Interfold.Bootstrapper.Phases.PublishPhase"/> driven against
/// a real DinD-bound bootstrapper. The pure path is unit-tested in
/// <c>PublishEnvPostProcessingTests</c>; these tests assert the file that <em>actually lands on
/// disk</em> after Aspire publish + our env post-processing matches the contract.
/// </summary>
/// <remarks>
/// All tests here run the <c>publish</c> command only (no <c>compose up</c>), so they don't
/// need the multi-minute health-wait budget that the launch-style tests do — they run in well
/// under 30 seconds each, fully in parallel with every other test in the assembly.
/// </remarks>
[RequiresDocker]
[ClassDataSource<UbuntuDinDFixture>(Shared = SharedType.PerTestSession)]
public class PublishIntegrationTests(UbuntuDinDFixture dinD)
{

    /// <summary>
    /// Documented set of keys we expect <c>PublishPhase.BuildEnvReplacements</c> to fill in the
    /// emitted <c>.env</c>. Mirror of the 6 parameter keys asserted by the unit test after the
    /// encryption-pepper / JWT / OAuth-secret / leaf-PFX-password migration into
    /// <c>internal.secrets</c> — keeping the list duplicated catches drift in either direction
    /// at integration time.
    /// </summary>
    private static readonly string[] ExpectedEnvParameterKeys =
    [
        "POSTGRES_USER",
        "POSTGRES_PASSWORD",
        "POSTGRES_INIT_PASSWORD",
        "SCYLLA_USER",
        "SCYLLA_PASSWORD",
        "ENCRYPTION_PRIVATE_KEY",
    ];

    [After(Test)]
    public Task DumpOnFailure(TestContext ctx) => DinDHookHelpers.DumpOnFailureAsync(dinD, ctx);

    [Test]
    public async Task BindMountPathsResolveAbsoluteInComposeEnv()
    {
        var (scratch, _) = await dinD.PublishAsync(nameof(BindMountPathsResolveAbsoluteInComposeEnv), TestConfigPaths.DefaultConfig);

        var envBytes = await dinD.CopyOutAsync($"{scratch.OutputDir}/.env");
        var env = DotEnvParser.ParseEnv(envBytes);

        // The compose graph emits one bind-mount key per service+target pair; Aspire names them
        // <SERVICE>_BINDMOUNTS__<N>. Single-mode scylla emits one rackdc mount + the api gets
        // one (/certs only — /keys was removed when JWT material moved into internal.secrets).
        var bindMountKeys = env
            .Where(kv => kv.Key.Contains("BIND", StringComparison.Ordinal))
            .ToList();
        await Assert.That(bindMountKeys.Count).IsGreaterThan(0)
            .Because("the published .env should contain at least one bind-mount entry");

        foreach (var (key, value) in bindMountKeys)
        {
            await Assert.That(value).IsNotEmpty()
                .Because($"bind-mount key {key} should not be blank in the .env");
            await Assert.That(value).StartsWith("/")
                .Because($"bind-mount source must be an absolute path: {key}={value}");
        }
    }

    [Test]
    public async Task CustomApiImageAppearsInGeneratedCompose()
    {
        // The default test fixture config sets apiImage to `interfold-api:test`, which differs
        // from the production default `ghcr.io/azyyyyyy/interfold-api:latest`. The compose YAML
        // must reference the override - if not, the rest of the pipeline would silently pull
        // from the public registry.
        var (scratch, _) = await dinD.PublishAsync(nameof(CustomApiImageAppearsInGeneratedCompose), TestConfigPaths.DefaultConfig);

        var composeBytes = await dinD.CopyOutAsync($"{scratch.OutputDir}/docker-compose.yaml");
        var compose = Encoding.UTF8.GetString(composeBytes);

        await Assert.That(compose).Contains("interfold-api:test")
            .Because("the custom apiImage from interfold.bootstrap.test.json should appear in the emitted compose");
        // The production default tag must NOT leak through when overridden.
        await Assert.That(compose).DoesNotContain("ghcr.io/azyyyyyy/interfold-api:latest")
            .Because("the default api image tag must not appear when apiImage is overridden");
    }

    [Test]
    public async Task EnvFileContainsAllRequiredKeys()
    {
        var (scratch, _) = await dinD.PublishAsync(nameof(EnvFileContainsAllRequiredKeys), TestConfigPaths.DefaultConfig);

        var envBytes = await dinD.CopyOutAsync($"{scratch.OutputDir}/.env");
        var env = DotEnvParser.ParseEnv(envBytes);

        foreach (var key in ExpectedEnvParameterKeys)
        {
            await Assert.That(env.ContainsKey(key)).IsTrue()
                .Because($"expected env key '{key}' missing from .env");
            await Assert.That(env[key]).IsNotEmpty()
                .Because($"env key '{key}' must be non-empty in the post-processed .env");
        }

        // OAuth client secrets + leaf PFX password must NOT appear in the .env any more —
        // they live inside internal.secrets and are loaded at API startup (via
        // SecretsBootstrapService and the one-shot Kestrel Npgsql loader).
        await Assert.That(env.ContainsKey("GOOGLE_OAUTH_CLIENT_SECRET")).IsFalse()
            .Because("the google oauth client secret must not leak into compose .env");
        await Assert.That(env.ContainsKey("DISCORD_OAUTH_CLIENT_SECRET")).IsFalse()
            .Because("the discord oauth client secret must not leak into compose .env");
        await Assert.That(env.ContainsKey("LEAF_PFX_PASSWORD")).IsFalse()
            .Because("the leaf PFX password must live in internal.secrets, not the .env");

        // Negative invariants — the admin passwords must NEVER appear in the published .env.
        await Assert.That(env.ContainsKey("POSTGRES_ADMIN_PASSWORD")).IsFalse()
            .Because("postgres admin credential must live in internal.secrets, not the .env");
        await Assert.That(env.ContainsKey("SCYLLA_ADMIN_PASSWORD")).IsFalse()
            .Because("scylla admin credential must live in internal.secrets, not the .env");
    }

}



