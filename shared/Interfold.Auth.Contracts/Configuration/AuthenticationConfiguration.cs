using System.ComponentModel.DataAnnotations;

namespace Interfold.Auth.Contracts.Configuration;

/// <summary>Authentication, OAuth, and JWT configuration. Env-bound (OCTOCON_/GUARDIAN_
/// prefixes); [Required] secret fields are patched from <c>internal.secrets</c> by
/// <c>AuthenticationSecretsPostConfigure</c> before <c>.ValidateOnStart()</c>.</summary>
public sealed class AuthenticationConfiguration
{
    public const string SectionName = "Octocon:Authentication";

    /// <summary>Env: OCTOCON_AUTH_CALLBACK_BASE_URL.</summary>
    public string? CallbackBaseUrl { get; set; }

    /// <summary>Deep-link HMAC signing secret. Sourced from
    /// <c>internal.secrets:auth:deep_link_secret</c>. Required.</summary>
    [Required(AllowEmptyStrings = false)]
    public string DeepLinkSecret { get; set; } = string.Empty;

    /// <summary>Env: OCTOCON_JWT_AUTHORITY.</summary>
    public string JwtAuthority { get; set; } = "";

    /// <summary>Env: OCTOCON_JWT_AUDIENCE.</summary>
    public string JwtAudience { get; set; } = "octocon";

    /// <summary>ES256 private key (PEM, SEC1) for token issuance. Sourced from
    /// <c>internal.secrets:auth:jwt_es256_private_pem</c>. Required.</summary>
    [Required(AllowEmptyStrings = false)]
    public string JwtEs256PrivateKeyPem { get; set; } = string.Empty;

    /// <summary>ES256 verification keys derived from JwtEs256PrivateKeyPem's public half.</summary>
    public string[]? JwtEs256VerificationKeyPems { get; set; }

    /// <summary>Static server-side E2E-derivation pepper. Sourced from
    /// <c>internal.secrets:encryption:pepper</c>. Required.</summary>
    [Required(AllowEmptyStrings = false)]
    public string EncryptionPepper { get; set; } = string.Empty;

    /// <summary>RSA-2048 JWT public key (PEM, SPKI). Derived from Rsa256PrivateKey; exposed
    /// via JWKS. Presence guaranteed transitively by the required private key.</summary>
    public string Rsa256PublicKey { get; set; } = string.Empty;

    /// <summary>RSA-2048 JWT private key (PEM, PKCS#8). Sourced from
    /// <c>internal.secrets:auth:jwt_rsa256_private_pem</c>. Required.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Rsa256PrivateKey { get; set; } = string.Empty;

    /// <summary>Env: OCTOCON_GOOGLE_OAUTH_CLIENT_ID.</summary>
    public string? GoogleOAuthClientId { get; set; }

    /// <summary>Env: OCTOCON_GOOGLE_OAUTH_CLIENT_SECRET.</summary>
    public string? GoogleOAuthClientSecret { get; set; }

    /// <summary>Env: OCTOCON_APPLE_OAUTH_CLIENT_ID.</summary>
    public string? AppleOAuthClientId { get; set; }

    /// <summary>Env: OCTOCON_APPLE_OAUTH_CLIENT_SECRET.</summary>
    public string? AppleOAuthClientSecret { get; set; }

    /// <summary>Env: OCTOCON_DISCORD_OAUTH_CLIENT_ID.</summary>
    public string? DiscordOAuthClientId { get; set; }

    /// <summary>Env: OCTOCON_DISCORD_OAUTH_CLIENT_SECRET.</summary>
    public string? DiscordOAuthClientSecret { get; set; }
}
