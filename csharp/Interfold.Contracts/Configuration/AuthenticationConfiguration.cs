using System.ComponentModel.DataAnnotations;

namespace Interfold.Contracts.Configuration;

/// <summary>
/// Authentication, OAuth, and JWT configuration.
/// Binds from environment variables with OCTOCON_ and GUARDIAN_ prefixes; the four
/// <see cref="RequiredAttribute"/>-annotated secret fields below are patched in from
/// <c>internal.secrets</c> by <c>AuthenticationSecretsPostConfigure</c> before
/// <c>.ValidateOnStart()</c> runs.
/// </summary>
public sealed class AuthenticationConfiguration
{
    public const string SectionName = "Octocon:Authentication";

    /// <summary>
    /// Base URL for OAuth callback handlers.
    /// Env: OCTOCON_AUTH_CALLBACK_BASE_URL
    /// </summary>
    public string? CallbackBaseUrl { get; set; }

    /// <summary>
    /// HMAC signing secret for the deep-link token exchange.
    /// Sourced from <c>internal.secrets</c> via <c>auth:deep_link_secret</c> — populated
    /// by <c>AuthenticationSecretsPostConfigure</c>. Never read from env. Required —
    /// a missing row fails <c>.ValidateOnStart()</c> at boot.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string DeepLinkSecret { get; set; } = string.Empty;

    /// <summary>
    /// JWT authority fallback
    /// Env: OCTOCON_JWT_AUTHORITY
    /// </summary>
    public string JwtAuthority { get; set; } = "";

    /// <summary>
    /// JWT audience
    /// Env: OCTOCON_JWT_AUDIENCE
    /// </summary>
    public string JwtAudience { get; set; } = "octocon";

    /// <summary>
    /// ES256 private key (PEM, SEC1) used for token issuance.
    /// Sourced from <c>internal.secrets</c> via <c>auth:jwt_es256_private_pem</c> —
    /// populated by <c>AuthenticationSecretsPostConfigure</c>. Never read from env.
    /// Required — a missing row fails <c>.ValidateOnStart()</c> at boot.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string JwtEs256PrivateKeyPem { get; set; } = string.Empty;

    /// <summary>
    /// ES256 verification keys (PEM) used for token signature validation. Populated by
    /// <c>AuthenticationSecretsPostConfigure</c> from the same private PEM as
    /// <see cref="JwtEs256PrivateKeyPem"/> — <c>ECDsa.ImportFromPem</c> reads the public
    /// half out of the private key.
    /// </summary>
    public string[]? JwtEs256VerificationKeyPems { get; set; }

    /// <summary>
    /// Static server-side pepper used by E2E key derivation. Sourced exclusively from
    /// <c>internal.secrets</c> via <c>encryption:pepper</c> — populated by
    /// <c>AuthenticationSecretsPostConfigure</c>. Never read from env. Required — a
    /// missing row fails <c>.ValidateOnStart()</c> at boot with the offending field named.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string EncryptionPepper { get; set; } = string.Empty;

    /// <summary>
    /// RSA-2048 JWT public key (PEM, SPKI). Derived in
    /// <c>AuthenticationSecretsPostConfigure</c> from <see cref="Rsa256PrivateKey"/>
    /// after the private PEM is patched from the store. Exposed via the API's JWKS
    /// endpoint. Not <c>[Required]</c> directly — its presence is guaranteed transitively
    /// by <see cref="Rsa256PrivateKey"/> being required.
    /// </summary>
    public string Rsa256PublicKey { get; set; } = string.Empty;

    /// <summary>
    /// RSA-2048 JWT private key (PEM, PKCS#8). Sourced from <c>internal.secrets</c> via
    /// <c>auth:jwt_rsa256_private_pem</c> — populated by
    /// <c>AuthenticationSecretsPostConfigure</c>. Never read from env. Required — a
    /// missing row fails <c>.ValidateOnStart()</c> at boot.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Rsa256PrivateKey { get; set; } = string.Empty;

    /// <summary>
    /// Google OAuth 2.0 client ID for backend token exchange.
    /// Env: OCTOCON_GOOGLE_OAUTH_CLIENT_ID
    /// </summary>
    public string? GoogleOAuthClientId { get; set; }

    /// <summary>
    /// Google OAuth 2.0 client secret for token exchange.
    /// Env: OCTOCON_GOOGLE_OAUTH_CLIENT_SECRET
    /// </summary>
    public string? GoogleOAuthClientSecret { get; set; }

    /// <summary>
    /// Apple OAuth 2.0 client ID for backend token exchange.
    /// Env: OCTOCON_APPLE_OAUTH_CLIENT_ID
    /// </summary>
    public string? AppleOAuthClientId { get; set; }

    /// <summary>
    /// Apple OAuth 2.0 client secret for token exchange.
    /// Env: OCTOCON_APPLE_OAUTH_CLIENT_SECRET
    /// </summary>
    public string? AppleOAuthClientSecret { get; set; }

    /// <summary>
    /// Discord OAuth 2.0 client ID for backend token exchange.
    /// Env: OCTOCON_DISCORD_OAUTH_CLIENT_ID
    /// </summary>
    public string? DiscordOAuthClientId { get; set; }

    /// <summary>
    /// Discord OAuth 2.0 client secret for token exchange.
    /// Env: OCTOCON_DISCORD_OAUTH_CLIENT_SECRET
    /// </summary>
    public string? DiscordOAuthClientSecret { get; set; }

    // Scheme names, provider authorization endpoints, and the static OAuth challenge
    // parameters (scopes / response_type / response_mode) are all baked into
    // OAuthChallengeServiceCollectionExtensions as constants — every provider serves a
    // single global URL and the scopes are tied to the data the callback handlers read,
    // so changing either requires a code change anyway. The only per-deployment value
    // the redirect URL still carries is client_id, which is injected from the
    // *OAuthClientId properties above at scheme-registration time.
}
