namespace Interfold.Contracts.Configuration;

/// <summary>
/// Strongly-typed options for the in-memory secrets seed used by the InMemory persistence
/// adapter. Bound from the <c>OCTOCON_INMEMORY_SECRETS_SEED__*</c> env-var family so an
/// external test harness (Kotlin Testcontainers, ad-hoc local container, etc.) can
/// bootstrap the published image without an in-process hook.
///
/// The <c>__</c>-to-<c>:</c> remap performed by
/// <see cref="Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationProvider"/>
/// on load means the underlying config keys are
/// <c>OCTOCON_INMEMORY_SECRETS_SEED:ENCRYPTION_PEPPER</c> etc.; see
/// <see cref="OctoconEnvKeys.InMemorySecretsSeedEncryptionPepper"/> for the full list.
/// Blank/missing values are legal and skipped silently by the seeding pass —
/// <c>SecretsBootstrapService</c> is the single source of fail-fast for the mandatory
/// rows (encryption pepper), so we deliberately do not duplicate that contract here.
/// </summary>
public sealed class InMemorySecretsSeedOptions
{
    public const string SectionName = "Octocon:InMemorySecretsSeed";

    /// <summary>
    /// Env: <c>OCTOCON_INMEMORY_SECRETS_SEED__ENCRYPTION_PEPPER</c>. Seed for
    /// <c>internal.secrets:encryption:pepper</c>. Blank leaves the row unset —
    /// SecretsBootstrapService will then fail-fast when it reads the empty value.
    /// </summary>
    public string? EncryptionPepper { get; set; }

    /// <summary>
    /// Env: <c>OCTOCON_INMEMORY_SECRETS_SEED__AUTH_JWT_ES256_PRIVATE_PEM</c>. Seed for
    /// <c>internal.secrets:auth:jwt_es256_private_pem</c>. Blank leaves the row unset;
    /// the API surfaces a signing failure on first JWT issuance instead of failing at boot.
    /// </summary>
    public string? AuthJwtEs256PrivatePem { get; set; }

    /// <summary>
    /// Env: <c>OCTOCON_INMEMORY_SECRETS_SEED__AUTH_DEEP_LINK_SECRET</c>. Seed for
    /// <c>internal.secrets:auth:deep_link_secret</c>. Blank leaves the row unset;
    /// deep-link HMAC verification then fails at first use rather than at boot.
    /// </summary>
    public string? AuthDeepLinkSecret { get; set; }

    /// <summary>
    /// Env: <c>OCTOCON_INMEMORY_SECRETS_SEED__AUTH_JWT_RSA256_PRIVATE_PEM</c>. Seed for
    /// <c>internal.secrets:auth:jwt_rsa256_private_pem</c>. Blank leaves the row unset.
    /// </summary>
    public string? AuthJwtRsa256PrivatePem { get; set; }
}
