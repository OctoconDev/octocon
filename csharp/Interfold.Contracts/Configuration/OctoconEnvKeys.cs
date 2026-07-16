namespace Interfold.Contracts.Configuration;

/// <summary>
/// Well-known <c>OCTOCON_*</c> environment-variable names read by <c>Interfold.Infrastructure</c>'s
/// options binders and written by <c>Interfold.Bootstrapper</c>'s <c>PublishPhase</c> (via
/// <c>InterfoldAppHost</c> parameter names, which Aspire upper-snake-cases with an
/// <c>OCTOCON_</c> prefix). Centralising the names here means the reader and the writer share
/// a single source of truth — a rename lands in one place instead of drifting silently between
/// the two projects.
/// </summary>
/// <remarks>
/// Values are the external contract: existing self-hosted deployments and CI pipelines
/// depend on these exact names in their <c>.env</c> files. CHANGING ANY VALUE HERE IS A
/// BREAKING CHANGE for operators.
/// </remarks>
public static class OctoconEnvKeys
{
    // --- Cluster / node role ---
    public const string NodeGroup = "OCTOCON_NODE_GROUP";

    // --- Persistence ---
    public const string Persistence = "OCTOCON_PERSISTENCE";
    public const string ScyllaKeyspace = "OCTOCON_SCYLLA_KEYSPACE";
    public const string PostgresConnection = "OCTOCON_POSTGRES_CONNECTION";
    public const string SingleScyllaInstance = "OCTOCON_SINGLE_SCYLLA_INSTANCE";
    public const string DbRetryAttempts = "OCTOCON_DB_RETRY_ATTEMPTS";
    public const string DbRetryInitialDelayMs = "OCTOCON_DB_RETRY_INITIAL_DELAY_MS";
    public const string DbRetryMaxDelayMs = "OCTOCON_DB_RETRY_MAX_DELAY_MS";
    public const string HydrationMaxConcurrency = "OCTOCON_HYDRATION_MAX_CONCURRENCY";

    // --- Scylla runtime overrides (test/dev host-port + contact-point overrides) ---
    // The production values live in internal.secrets (scylla:contact_points, scylla:port);
    // these env vars only exist so integration-test containers can point the client at a
    // host-published port that differs from the store-supplied cluster address.
    public const string ScyllaContactPoints = "OCTOCON_SCYLLA_CONTACT_POINTS";
    public const string ScyllaPort = "OCTOCON_SCYLLA_PORT";

    // --- In-memory secrets seeding (test/self-host bootstrap) ---
    // Operator-facing env vars: OCTOCON_INMEMORY_SECRETS_SEED__<SUFFIX>. The `__` -> `:`
    // remap is applied by EnvironmentVariablesConfigurationProvider on load, so lookups
    // go through the `Octocon:InMemorySecretsSeed:*` config-key hierarchy at read time.
    public const string InMemorySecretsSeedEncryptionPepper =
        "OCTOCON_INMEMORY_SECRETS_SEED:ENCRYPTION_PEPPER";
    public const string InMemorySecretsSeedAuthJwtEs256PrivatePem =
        "OCTOCON_INMEMORY_SECRETS_SEED:AUTH_JWT_ES256_PRIVATE_PEM";
    public const string InMemorySecretsSeedAuthDeepLinkSecret =
        "OCTOCON_INMEMORY_SECRETS_SEED:AUTH_DEEP_LINK_SECRET";
    public const string InMemorySecretsSeedAuthJwtRsa256PrivatePem =
        "OCTOCON_INMEMORY_SECRETS_SEED:AUTH_JWT_RSA256_PRIVATE_PEM";

    // --- Authentication ---
    public const string AuthCallbackBaseUrl = "OCTOCON_AUTH_CALLBACK_BASE_URL";
    public const string JwtAuthority = "OCTOCON_JWT_AUTHORITY";
    public const string JwtAudience = "OCTOCON_JWT_AUDIENCE";

    public const string DiscordOAuthClientId = "OCTOCON_DISCORD_OAUTH_CLIENT_ID";
    public const string DiscordOAuthClientSecret = "OCTOCON_DISCORD_OAUTH_CLIENT_SECRET";
    public const string GoogleOAuthClientId = "OCTOCON_GOOGLE_OAUTH_CLIENT_ID";
    public const string GoogleOAuthClientSecret = "OCTOCON_GOOGLE_OAUTH_CLIENT_SECRET";
    public const string AppleOAuthClientId = "OCTOCON_APPLE_OAUTH_CLIENT_ID";
    public const string AppleOAuthClientSecret = "OCTOCON_APPLE_OAUTH_CLIENT_SECRET";

    public const string CorsAllowedOrigins = "OCTOCON_CORS_ALLOWED_ORIGINS";

    // --- Observability ---
    public const string OtlpEndpoint = "OCTOCON_OTLP_ENDPOINT";

    // --- Trust artefacts ---
    public const string TrustRootCaPath = "OCTOCON_TRUST_ROOT_CA_PATH";
    public const string TrustRootCaFingerprintPath = "OCTOCON_TRUST_ROOT_CA_FINGERPRINT_PATH";

    // --- Storage ---
    public const string AvatarStorageRoot = "OCTOCON_AVATAR_STORAGE_ROOT";
    public const string AvatarPublicBase = "OCTOCON_AVATAR_PUBLIC_BASE";

    // --- Socket tuning ---
    public const string SocketBatchBytesThreshold = "OCTOCON_SOCKET_BATCH_BYTES_THRESHOLD";

    // --- Testing (Interfold.IntegrationTests / TestingConfiguration only) ---
    public const string RunApiIntegration = "OCTOCON_RUN_API_INTEGRATION";
    public const string RunLiveIntegration = "OCTOCON_RUN_LIVE_INTEGRATION";
    public const string TestScyllaContactPoints = "OCTOCON_TEST_SCYLLA_CONTACT_POINTS";
    public const string TestScyllaUsername = "OCTOCON_TEST_SCYLLA_USERNAME";
    public const string TestScyllaPassword = "OCTOCON_TEST_SCYLLA_PASSWORD";
    public const string TestRegion = "OCTOCON_TEST_REGION";

    // --- Platform-specific (Fly.io process-group discovery — read as a fallback for NodeGroup) ---
    public const string FlyProcessGroup = "FLY_PROCESS_GROUP";
}
