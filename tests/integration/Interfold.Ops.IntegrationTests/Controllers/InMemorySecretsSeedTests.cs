using System.Net;
using System.Net.Http.Json;
using Interfold.Shared.Contracts;
using Interfold.IntegrationTests.TestServices;

namespace Interfold.IntegrationTests.Controllers;

/// <summary>
/// Regression coverage that documents the env-var seed contract for the in-memory
/// <c>ISecretsStore</c> with a self-describing test name: a future refactor that breaks
/// the <c>OCTOCON_INMEMORY_SECRETS_SEED__*</c> wiring fails here with a clear signal,
/// rather than a flood of red across every other in-memory integration test.
///
/// The factory is constructed inline (no <c>[ClassDataSource]</c>) so the test boots a
/// fresh host whose only secrets material flows through the production env-var path
/// registered in <see cref="Interfold.Infrastructure.InMemory.InMemoryServiceCollectionExtensions"/>
/// — exactly what the published image does when an external runner (e.g. the Kotlin
/// Testcontainers harness) drives it as a separate process.
/// </summary>
public class InMemorySecretsSeedTests : BaseEndpointTest
{
    // Operator-facing env-var names (the `__` form). The .NET EnvironmentVariablesConfigurationProvider
    // rewrites `__` to `:` on load, so SeedFromConfig in InMemoryServiceCollectionExtensions looks up
    // the `:`-normalised key — but the contract operators set in their docker run / Testcontainers
    // env is the `__` form below, which is exactly what we mutate here to lock that real path.
    private const string EnvKeyEncryptionPepper       = "OCTOCON_INMEMORY_SECRETS_SEED__ENCRYPTION_PEPPER";
    private const string EnvKeyJwtEs256PrivatePem     = "OCTOCON_INMEMORY_SECRETS_SEED__AUTH_JWT_ES256_PRIVATE_PEM";
    private const string EnvKeyDeepLinkSecret         = "OCTOCON_INMEMORY_SECRETS_SEED__AUTH_DEEP_LINK_SECRET";
    private const string EnvKeyJwtRsa256PrivatePem    = "OCTOCON_INMEMORY_SECRETS_SEED__AUTH_JWT_RSA256_PRIVATE_PEM";

    [Test]
    public async Task Api_InMemorySecretsSeed_PatchesAuthFromEnvVars()
    {
        await using var factory = new InterfoldWebApplicationFactory(PersistenceMode.InMemory);
        using var client = factory.CreateClient();

        var principalId = TestIds.NewSystemId("sys-secrets-seed", maxLen: 24);

        // POST /api/settings/username is a [Authorize]-gated route. Reaching 204 NoContent
        // proves end-to-end that:
        //   (a) the host started without tripping SecretsBootstrapService's hard
        //       fail-fast on `encryption:pepper` (so OCTOCON_INMEMORY_SECRETS_SEED__ENCRYPTION_PEPPER
        //       was honoured), and
        //   (b) the JWT minted by CreateToken with TestDbCredentials.JwtEs256PrivateKeyPem
        //       verifies against AuthenticationConfiguration.JwtEs256VerificationKeyPems —
        //       which in turn proves OCTOCON_INMEMORY_SECRETS_SEED__AUTH_JWT_ES256_PRIVATE_PEM
        //       was seeded into the store and patched onto AuthenticationConfiguration by
        //       SecretsBootstrapService at startup.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/settings/username")
        {
            Content = JsonContent.Create(new { username = principalId })
        };
        AttachPrincipalAuth(request, client, principalId);

        var response = await client.SendAsync(request);

        await Assert.That(response.StatusCode)
            .IsEqualTo(HttpStatusCode.NoContent)
            .Because($"Expected 204 NoContent, got {(int)response.StatusCode}. Body: {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>
    /// Locks the real env-var ingestion path that <c>octocon-app</c>'s Testcontainers harness and
    /// the published-image docker run both rely on. The sibling test above only exercises the
    /// <c>FactoryConfigurationProvider</c> shortcut — that path stores keys verbatim and so happily
    /// matched the literal <c>__</c>-form lookup even while real env vars (which the
    /// <c>EnvironmentVariablesConfigurationProvider</c> rewrites to <c>:</c>) silently missed.
    /// This test mutates <see cref="Environment.SetEnvironmentVariable(string,string)"/> with the
    /// operator-facing <c>__</c> form and builds the factory with the in-fixture <c>:</c>-form
    /// seeds suppressed (<c>seedInMemorySecretsFromFactoryConfig: false</c>), so the only route
    /// from env var to <c>InMemorySecretsStore</c> is the production
    /// <c>EnvironmentVariablesConfigurationProvider</c> → <c>SeedFromConfig</c> chain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Environment.SetEnvironmentVariable</c> is process-global, so the <c>NotInParallel</c>
    /// key serialises this test against any other env-var-sensitive test in the suite (none today,
    /// but the named key is the established pattern in <c>AvatarSourceTests</c> /
    /// <c>SettingsControllerTests</c>). Prior values are captured up front and restored in the
    /// <c>finally</c> so re-runs in the same test session don't leak state — important because
    /// <c>EnvironmentVariablesConfigurationProvider</c> snapshots env vars at host build time, so a
    /// leaked value would silently flip the behaviour of a later host build in the same process.
    /// </para>
    /// </remarks>
    [Test, NotInParallel("inmemory-secrets-seed-envvars")]
    public async Task Api_InMemorySecretsSeed_PatchesAuthFromRealEnvVars()
    {
        var prior = new[]
        {
            (Key: EnvKeyEncryptionPepper,    Value: Environment.GetEnvironmentVariable(EnvKeyEncryptionPepper)),
            (Key: EnvKeyJwtEs256PrivatePem,  Value: Environment.GetEnvironmentVariable(EnvKeyJwtEs256PrivatePem)),
            (Key: EnvKeyDeepLinkSecret,      Value: Environment.GetEnvironmentVariable(EnvKeyDeepLinkSecret)),
            (Key: EnvKeyJwtRsa256PrivatePem, Value: Environment.GetEnvironmentVariable(EnvKeyJwtRsa256PrivatePem)),
        };

        try
        {
            Environment.SetEnvironmentVariable(EnvKeyEncryptionPepper,    "TEST");
            Environment.SetEnvironmentVariable(EnvKeyJwtEs256PrivatePem,  TestDbCredentials.JwtEs256PrivateKeyPem);
            Environment.SetEnvironmentVariable(EnvKeyDeepLinkSecret,      TestDbCredentials.DeepLinkSecret);
            Environment.SetEnvironmentVariable(EnvKeyJwtRsa256PrivatePem, TestDbCredentials.JwtRsa256PrivateKeyPem);

            // seedInMemorySecretsFromFactoryConfig: false suppresses the constructor's `:`-form
            // FactoryConfigurationProvider writes for these four keys, so SeedFromConfig has no
            // shortcut and must resolve them from the env-var-backed configuration provider — i.e.
            // the same code path the published image exercises in production.
            await using var factory = new InterfoldWebApplicationFactory(
                PersistenceMode.InMemory,
                seedInMemorySecretsFromFactoryConfig: false);
            using var client = factory.CreateClient();

            var principalId = TestIds.NewSystemId("sys-secrets-env", maxLen: 24);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/settings/username")
            {
                Content = JsonContent.Create(new { username = principalId })
            };
            AttachPrincipalAuth(request, client, principalId);

            var response = await client.SendAsync(request);

            await Assert.That(response.StatusCode)
                .IsEqualTo(HttpStatusCode.NoContent)
                .Because($"Expected 204 NoContent, got {(int)response.StatusCode}. Body: {await response.Content.ReadAsStringAsync()}");
        }
        finally
        {
            foreach (var (key, value) in prior)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
