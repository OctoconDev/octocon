using System.Security.Cryptography;
using Interfold.Api.Services.Secrets;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Mocks;

namespace Interfold.Api.UnitTests.Options;

// Contract for AuthenticationSecretsPostConfigure: seven auth rows from the snapshot
// flow onto AuthenticationConfiguration; a missing mandatory row surfaces as a
// boot-time OptionsValidationException with the offending field named. Pure-C# (no
// host, no ISecretsStore, no DI hosting stack) via the SecretsSnapshotMock helper.
public sealed class AuthenticationSecretsPostConfigureTests
{
    // Deterministic PEMs generated once per run; cheap enough that we don't cache
    // across cases, expensive enough that we don't want them per-assertion.
    private static readonly (string PrivatePem, string PublicPem) Rsa = GenerateRsaKeypair();
    private static readonly (string PrivatePem, string PublicPem) Es = GenerateEs256Keypair();

    [Test]
    public async Task PostConfigure_SnapshotHasAllRows_PatchesAllFields()
    {
        var options = SecretsSnapshotMock.Empty()
            .With(SecretsStoreKeys.OAuthGoogleClientSecret,  "google-secret")
            .With(SecretsStoreKeys.OAuthDiscordClientSecret, "discord-secret")
            .With(SecretsStoreKeys.OAuthAppleClientSecret,   "apple-secret")
            .With(SecretsStoreKeys.EncryptionPepper,         "test-pepper")
            .With(SecretsStoreKeys.AuthDeepLinkSecret,       "test-deep-link")
            .With(SecretsStoreKeys.AuthJwtRsa256PrivatePem,  Rsa.PrivatePem)
            .With(SecretsStoreKeys.AuthJwtEs256PrivatePem,   Es.PrivatePem)
            .ApplyPostConfigure<AuthenticationSecretsPostConfigure, AuthenticationConfiguration>();

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

    // Framework spec allows re-resolution on config reload; two PostConfigure passes
    // must not duplicate the ES256 verification-key entry.
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

    [Test]
    public async Task PostConfigure_MissingMandatoryRow_TripsValidation()
    {
        var services = new ServiceCollection();
        var snapshot = SecretsSnapshotMock.Empty();
        services.AddSingleton<ISecretsSnapshot>(snapshot.Object);
        services.AddSingleton<IPostConfigureOptions<AuthenticationConfiguration>, AuthenticationSecretsPostConfigure>();
        services.AddOptions<AuthenticationConfiguration>()
            .Configure(_ => { })
            .ValidateDataAnnotations();

        await using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<AuthenticationConfiguration>>();

        var ex = Assert.Throws<OptionsValidationException>(() => _ = monitor.CurrentValue);
        await Assert.That(ex!.Failures)
            .Contains(f => f.Contains("EncryptionPepper", StringComparison.Ordinal))
            .Because("The DataAnnotations validator must call out EncryptionPepper by name — operators need the field name in logs to find the missing internal.secrets row without diffing configuration.");
    }

    // Separate from the pepper case because KDF vs JWT are distinct downstream systems.
    [Test]
    public async Task PostConfigure_MissingEs256Pem_TripsValidation()
    {
        var services = new ServiceCollection();
        // Populate everything except ES256 so the validator can only blame that field.
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

    [Test]
    public async Task PostConfigure_NamedInstance_LeavesUntouched()
    {
        var options = SecretsSnapshotMock.Empty()
            .With(SecretsStoreKeys.EncryptionPepper, "leaked-if-named")
            .ApplyPostConfigure<AuthenticationSecretsPostConfigure, AuthenticationConfiguration>(name: "other-name");

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
