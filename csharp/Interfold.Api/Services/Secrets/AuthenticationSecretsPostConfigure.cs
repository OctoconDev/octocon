using System.Security.Cryptography;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Secrets;
using Microsoft.Extensions.Options;

namespace Interfold.Api.Services.Secrets;

/// <summary>
/// Post-configure step that layers <c>internal.secrets</c>-sourced values on top of the
/// env-bound <see cref="AuthenticationConfiguration"/> snapshot produced by
/// <c>ApplyAuthentication</c> in <c>ConfigurationServiceCollectionExtensions</c>.
///
/// <para>
/// Runs inside the options factory pipeline after <c>IConfigureOptions&lt;T&gt;</c>, before
/// <c>.ValidateDataAnnotations()</c>. That ordering matters: the <c>[Required]</c> attributes
/// on <see cref="AuthenticationConfiguration.EncryptionPepper"/>,
/// <see cref="AuthenticationConfiguration.DeepLinkSecret"/>,
/// <see cref="AuthenticationConfiguration.JwtEs256PrivateKeyPem"/>, and
/// <see cref="AuthenticationConfiguration.Rsa256PrivateKey"/> only pass when this class has
/// filled them from the snapshot. If any mandatory row is missing at the store,
/// <c>.ValidateOnStart()</c> throws a boot-time <see cref="OptionsValidationException"/>
/// with the offending field named.
/// </para>
///
/// <para>
/// OAuth client secrets stay nullable (deploy-time opt-in). Absent rows leave the property
/// null and the matching challenge scheme skips registration in
/// <c>AddInterfoldAuthChallengeSchemes</c>.
/// </para>
/// </summary>
internal sealed class AuthenticationSecretsPostConfigure(ISecretsSnapshot snapshot)
    : IPostConfigureOptions<AuthenticationConfiguration>
{
    private static readonly (SecretsStoreKey Key, Action<AuthenticationConfiguration, string> Patch)[] AuthSecretMappings =
    [
        (SecretsStoreKeys.OAuthGoogleClientSecret,  (auth, v) => auth.GoogleOAuthClientSecret = v),
        (SecretsStoreKeys.OAuthDiscordClientSecret, (auth, v) => auth.DiscordOAuthClientSecret = v),
        (SecretsStoreKeys.OAuthAppleClientSecret,   (auth, v) => auth.AppleOAuthClientSecret = v),
        (SecretsStoreKeys.EncryptionPepper,         (auth, v) => auth.EncryptionPepper = v),
        (SecretsStoreKeys.AuthDeepLinkSecret,       (auth, v) => auth.DeepLinkSecret = v),
        (SecretsStoreKeys.AuthJwtRsa256PrivatePem,  PatchRsa256),
        (SecretsStoreKeys.AuthJwtEs256PrivatePem,   PatchEs256),
    ];

    public void PostConfigure(string? name, AuthenticationConfiguration options)
    {
        // Only patch the default named instance — every consumer resolves the default
        // options bucket, and applying to named buckets would smear secrets across
        // logically-separate configurations.
        if (name != Options.DefaultName)
        {
            return;
        }

        foreach (var (key, patch) in AuthSecretMappings)
        {
            var value = snapshot.Get(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                patch(options, value);
            }
        }

        // Note: the mandatory-field enforcement lives on the [Required] attributes in
        // AuthenticationConfiguration and .ValidateOnStart() in
        // ConfigurationServiceCollectionExtensions. Raising here would fire twice for tests
        // that rebuild options after mutation, which was the exact fragility the SBS
        // fail-fast introduced.
    }

    /// <summary>
    /// Assigns the RSA-2048 JWT private key (PEM, PKCS#8) and derives the SPKI public PEM
    /// in place. The public half is what <c>SettingsController</c> hands out via the JWKS
    /// endpoint and what <c>RecoveryCodeResolver</c> wraps for envelope encryption.
    /// </summary>
    private static void PatchRsa256(AuthenticationConfiguration auth, string privatePem)
    {
        auth.Rsa256PrivateKey = privatePem;
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privatePem.AsSpan());
        auth.Rsa256PublicKey = rsa.ExportSubjectPublicKeyInfoPem();
    }

    /// <summary>
    /// Assigns the ES256 JWT private key (PEM, SEC1) and seeds the verification array with
    /// the same PEM. <see cref="ECDsa.ImportFromPem"/> reads the public half out of the
    /// private PEM at verify time, so a single row covers both signing and validation.
    /// </summary>
    private static void PatchEs256(AuthenticationConfiguration auth, string privatePem)
    {
        auth.JwtEs256PrivateKeyPem = privatePem;
        auth.JwtEs256VerificationKeyPems = [privatePem];
    }
}
