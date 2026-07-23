namespace Interfold.Shared.Contracts.Configuration;

/// <summary>In-memory-adapter secrets seed bound from <c>OCTOCON_INMEMORY_SECRETS_SEED__*</c>
/// env vars, so external test harnesses can bootstrap the published image without an
/// in-process hook. Blanks are silently skipped; <c>SecretsBootstrapService</c> is the
/// single source of fail-fast for mandatory rows.</summary>
public sealed class InMemorySecretsSeedOptions
{
    public const string SectionName = "Octocon:InMemorySecretsSeed";

    /// <summary>Env: OCTOCON_INMEMORY_SECRETS_SEED__ENCRYPTION_PEPPER.</summary>
    public string? EncryptionPepper { get; set; }

    /// <summary>Env: OCTOCON_INMEMORY_SECRETS_SEED__AUTH_JWT_ES256_PRIVATE_PEM.</summary>
    public string? AuthJwtEs256PrivatePem { get; set; }

    /// <summary>Env: OCTOCON_INMEMORY_SECRETS_SEED__AUTH_DEEP_LINK_SECRET.</summary>
    public string? AuthDeepLinkSecret { get; set; }

    /// <summary>Env: OCTOCON_INMEMORY_SECRETS_SEED__AUTH_JWT_RSA256_PRIVATE_PEM.</summary>
    public string? AuthJwtRsa256PrivatePem { get; set; }
}
