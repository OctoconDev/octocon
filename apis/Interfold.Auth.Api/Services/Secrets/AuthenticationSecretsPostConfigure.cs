using System.Security.Cryptography;
using Interfold.Auth.Contracts.Configuration;
using Interfold.Shared.Api.Services.Secrets;
using Interfold.Shared.Contracts.Secrets;
using Microsoft.Extensions.Options;

namespace Interfold.Auth.Api.Services.Secrets;

/// <summary>Layers <c>internal.secrets</c> rows onto the env-bound
/// <see cref="AuthenticationConfiguration"/>. Runs post-<c>IConfigureOptions</c>,
/// pre-<c>ValidateDataAnnotations</c> so the [Required] fields
/// (<see cref="AuthenticationConfiguration.EncryptionPepper"/>,
/// <see cref="AuthenticationConfiguration.DeepLinkSecret"/>,
/// <see cref="AuthenticationConfiguration.JwtEs256PrivateKeyPem"/>,
/// <see cref="AuthenticationConfiguration.Rsa256PrivateKey"/>) are populated before
/// <c>.ValidateOnStart()</c>. Absent OAuth client secrets stay null (skips scheme
/// registration).</summary>
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
        // Default bucket only — every consumer resolves it, and named buckets would smear
        // secrets across logically-separate configurations.
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
    }

    /// <summary>Assigns the RSA-2048 private PEM and derives the SPKI public PEM served
    /// via JWKS.</summary>
    private static void PatchRsa256(AuthenticationConfiguration auth, string privatePem)
    {
        auth.Rsa256PrivateKey = privatePem;
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privatePem.AsSpan());
        auth.Rsa256PublicKey = rsa.ExportSubjectPublicKeyInfoPem();
    }

    /// <summary>Assigns the ES256 private PEM and seeds the verification array with it —
    /// <see cref="ECDsa.ImportFromPem"/> extracts the public half at verify time.</summary>
    private static void PatchEs256(AuthenticationConfiguration auth, string privatePem)
    {
        auth.JwtEs256PrivateKeyPem = privatePem;
        auth.JwtEs256VerificationKeyPems = [privatePem];
    }
}
