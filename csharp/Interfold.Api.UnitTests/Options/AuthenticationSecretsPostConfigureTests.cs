using System.Security.Cryptography;
using Interfold.Api.Services.Secrets;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Mocks;

namespace Interfold.Api.UnitTests.Options;

/// <summary>
/// Locks the <see cref="AuthenticationSecretsPostConfigure"/> contract: it reads the
/// seven auth rows from the snapshot, applies them into
/// <see cref="AuthenticationConfiguration"/>, and lets the <c>[Required]</c> +
/// <c>ValidateDataAnnotations</c> pipeline turn a missing mandatory row into a
/// boot-time <see cref="OptionsValidationException"/> with the offending field named.
///
/// <para>
/// Uses a <see cref="StubSecretsSnapshot"/> in place of the loader so these stay
/// pure-C# tests — no host, no <see cref="ISecretsStore"/>, no DI hosting stack.
/// </para>
/// </summary>
public sealed class AuthenticationSecretsPostConfigureTests
{
    // Deterministic PEMs generated once per test run — cheap enough that we don't need to
    // cache them across cases, but expensive enough that we don't want them per-assertion.
    private static readonly (string PrivatePem, string PublicPem) Rsa = GenerateRsaKeypair();
    private static readonly (string PrivatePem, string PublicPem) Es = GenerateEs256Keypair();

    /// <summary>
    /// Happy path: every row present in the snapshot lands on the matching
    /// <see cref="AuthenticationConfiguration"/> property. This covers the seven-row
    /// mapping table verbatim so a future refactor that drops or renames a row fails
    /// here instead of surfacing as a silent 401 in production.
    /// </summary>
    [Test]
    public async Task PostConfigure_SnapshotHasAllRows_PatchesAllFields()
    {
        var snapshot = SecretsSnapshotMock.Empty()
            .With(SecretsStoreKeys.OAuthGoogleClientSecret,  "google-secret")
            .With(SecretsStoreKeys.OAuthDiscordClientSecret, "discord-secret")
            .With(SecretsStoreKeys.OAuthAppleClientSecret,   "apple-secret")
            .With(SecretsStoreKeys.EncryptionPepper,         "test-pepper")
            .With(SecretsStoreKeys.AuthDeepLinkSecret,       "test-deep-link")
            .With(SecretsStoreKeys.AuthJwtRsa256PrivatePem,  Rsa.PrivatePem)
            .With(SecretsStoreKeys.AuthJwtEs256PrivatePem,   Es.PrivatePem);
        var patcher = new AuthenticationSecretsPostConfigure(snapshot.Object);
        var options = new AuthenticationConfiguration();

        patcher.PostConfigure(Microsoft.Extensions.Options.Options.DefaultName, options);

        using (Assert.Multiple())
        {
            await Assert.That(options.GoogleOAuthClientSecret).IsEqualTo("google-secret")
                .Because("The Google OAuth client secret row must land on the matching nullable field so /oauth/google callbacks can complete the token exchange.");
            await Assert.That(options.DiscordOAuthClientSecret).IsEqualTo("discord-secret")
                .Because("Discord OAuth client secret parity — every OAuth provider needs its secret patched from the same code path.");
            await Assert.That(options.AppleOAuthClientSecret).IsEqualTo("apple-secret")
                .Because("Apple OAuth client secret parity.");
            await Assert.That(options.EncryptionPepper).IsEqualTo("test-pepper")
                .Because("The encryption pepper must move from snapshot to options; downstream KDFs read it via IOptionsMonitor at request time.");
            await Assert.That(options.DeepLinkSecret).IsEqualTo("test-deep-link")
                .Because("Deep-link HMAC secret must survive PostConfigure so phase-F token exchange keeps working.");
            await Assert.That(options.Rsa256PrivateKey).IsEqualTo(Rsa.PrivatePem)
                .Because("RSA-2048 private PEM must be assigned verbatim so PatchRsa256 can derive the SPKI public half.");
            await Assert.That(options.Rsa256PublicKey).IsEqualTo(Rsa.PublicPem)
                .Because("PatchRsa256 derives the SPKI public half in place — the JWKS endpoint reads this property.");
            await Assert.That(options.JwtEs256PrivateKeyPem).IsEqualTo(Es.PrivatePem)
                .Because("ES256 signing PEM must be assigned so AuthHelper.CreateToken can mint fresh tokens.");
            await Assert.That(options.JwtEs256VerificationKeyPems)
                .IsNotNull()
                .And
                .HasSingleItem()
                .Because("PatchEs256 seeds a single-element verification array from the private PEM (ECDsa.ImportFromPem extracts the public half at verify time).");
        }
    }

    /// <summary>
    /// Idempotency: calling <c>PostConfigure</c> twice on the same instance must not
    /// duplicate the ES256 verification-key entry. The <c>OptionsFactory</c> resolves
    /// each named instance once, but the framework spec permits re-resolution on
    /// configuration reload; test-fixture recomposition can trigger the same path.
    /// </summary>
    [Test]
    public async Task PostConfigure_IdempotentAcrossResolves()
    {
        var snapshot = SecretsSnapshotMock.Empty()
            .With(SecretsStoreKeys.EncryptionPepper,        "pepper")
            .With(SecretsStoreKeys.AuthDeepLinkSecret,      "dl")
            .With(SecretsStoreKeys.AuthJwtRsa256PrivatePem, Rsa.PrivatePem)
            .With(SecretsStoreKeys.AuthJwtEs256PrivatePem,  Es.PrivatePem);
        var patcher = new AuthenticationSecretsPostConfigure(snapshot.Object);
        var options = new AuthenticationConfiguration();

        patcher.PostConfigure(Microsoft.Extensions.Options.Options.DefaultName, options);
        patcher.PostConfigure(Microsoft.Extensions.Options.Options.DefaultName, options);

        await Assert.That(options.JwtEs256VerificationKeyPems)
            .IsNotNull()
            .And
            .HasSingleItem()
            .Because("Two PostConfigure passes must leave the verification-key array with a single entry — re-applying the same PEM would grow the array unboundedly on OptionsMonitor.OnChange, breaking ES256 signature validation ordering.");
    }

    /// <summary>
    /// Missing-mandatory guard: an empty snapshot leaves the four <c>[Required]</c>
    /// fields at their empty-string defaults, and resolving the options through the
    /// full <c>ValidateDataAnnotations()</c> pipeline surfaces the missing rows as an
    /// <see cref="OptionsValidationException"/>. This replaces the ad-hoc
    /// <c>InvalidOperationException</c> the old <c>SecretsBootstrapService</c> raised.
    /// </summary>
    [Test]
    public async Task PostConfigure_MissingMandatoryRow_TripsValidation()
    {
        var services = new ServiceCollection();
        var snapshot = SecretsSnapshotMock.Empty(); // empty — no mandatory rows populated
        services.AddSingleton<ISecretsSnapshot>(snapshot.Object);
        services.AddSingleton<IPostConfigureOptions<AuthenticationConfiguration>, AuthenticationSecretsPostConfigure>();
        services.AddOptions<AuthenticationConfiguration>()
            .Configure(_ => { /* no-op initial bind; PostConfigure has nothing to fold in */ })
            .ValidateDataAnnotations();

        await using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<AuthenticationConfiguration>>();

        var ex = Assert.Throws<OptionsValidationException>(() => _ = monitor.CurrentValue);
        await Assert.That(ex!.Failures)
            .Contains(f => f.Contains("EncryptionPepper", StringComparison.Ordinal))
            .Because("The DataAnnotations validator must call out EncryptionPepper by name — operators need the field name in logs to find the missing internal.secrets row without diffing configuration.");
    }

    /// <summary>
    /// Missing-ES256 sister case: same missing-row shape but for the ES256 private PEM,
    /// which is what actually breaks JWT issuance. Kept separate from the pepper case
    /// because the two failure modes surface in different downstream systems (KDF vs
    /// JWT), and we want a distinct regression signal for each.
    /// </summary>
    [Test]
    public async Task PostConfigure_MissingEs256Pem_TripsValidation()
    {
        var services = new ServiceCollection();
        // Populate every mandatory field EXCEPT the ES256 PEM so the validator can only
        // blame the missing field.
        var snapshot = SecretsSnapshotMock.Empty()
            .With(SecretsStoreKeys.EncryptionPepper,        "pepper")
            .With(SecretsStoreKeys.AuthDeepLinkSecret,      "dl")
            .With(SecretsStoreKeys.AuthJwtRsa256PrivatePem, Rsa.PrivatePem);
        services.AddSingleton<ISecretsSnapshot>(snapshot.Object);
        services.AddSingleton<IPostConfigureOptions<AuthenticationConfiguration>, AuthenticationSecretsPostConfigure>();
        services.AddOptions<AuthenticationConfiguration>()
            .Configure(_ => { })
            .ValidateDataAnnotations();

        await using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<AuthenticationConfiguration>>();

        var ex = Assert.Throws<OptionsValidationException>(() => _ = monitor.CurrentValue);
        await Assert.That(ex!.Failures)
            .Contains(f => f.Contains("JwtEs256PrivateKeyPem", StringComparison.Ordinal))
            .Because("A missing ES256 PEM must fail validation naming the field so the operator connects the log message to the internal.secrets:auth:jwt_es256_private_pem row.");
    }

    /// <summary>
    /// Named-instance guard: PostConfigure must only patch the default options bucket.
    /// A named instance (e.g. one created by a hypothetical multi-tenant scenario) must
    /// stay untouched so its own configuration doesn't leak the default's secrets.
    /// </summary>
    [Test]
    public async Task PostConfigure_NamedInstance_LeavesUntouched()
    {
        var snapshot = SecretsSnapshotMock.Empty()
            .With(SecretsStoreKeys.EncryptionPepper, "leaked-if-named");
        var patcher = new AuthenticationSecretsPostConfigure(snapshot.Object);
        var options = new AuthenticationConfiguration();

        patcher.PostConfigure("other-name", options);

        await Assert.That(options.EncryptionPepper).IsEqualTo(string.Empty)
            .Because("PostConfigure guards on Options.DefaultName so secrets never bleed into an unrelated named options bucket.");
    }

    private static (string PrivatePem, string PublicPem) GenerateRsaKeypair()
    {
        using var rsa = RSA.Create(2048);
        return (rsa.ExportPkcs8PrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());
    }

    private static (string PrivatePem, string PublicPem) GenerateEs256Keypair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ecdsa.ExportECPrivateKeyPem(), ecdsa.ExportSubjectPublicKeyInfoPem());
    }

}
