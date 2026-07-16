namespace Interfold.Contracts.Secrets;

/// <summary>
/// Typed row key for <c>internal.secrets</c>. Defined in this file (not a separate one)
/// because the file is compiled into both Interfold.Contracts and — via a linked
/// <c>&lt;Compile&gt;</c> item — Interfold.DatabaseBootstrap; keeping struct and registry
/// together preserves that single-source arrangement. Unwrap <see cref="Value"/> at DB
/// parameters and dictionary keys.
/// </summary>
#if INTERFOLD_DBBOOT_LINKED
internal
#else
public
#endif
readonly record struct SecretsStoreKey
{
    public string Value { get; }

    public SecretsStoreKey(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public override string ToString() => Value;
}

/// <summary>
/// Well-known row keys stored in <c>internal.secrets</c> and read back through
/// <see cref="ISecretsStore"/>. Centralising the strings here keeps the seeding side
/// (<c>Interfold.DatabaseBootstrap.SeedKeys</c>) and the runtime consumers (Infrastructure
/// DI wiring, <c>SecretsBootstrapService</c>, <c>FirebaseFCMService</c>) from drifting.
/// The values are the persisted wire format — CHANGING ANY VALUE HERE IS A BREAKING
/// SCHEMA CHANGE for existing self-hosted deployments.
/// </summary>
/// <remarks>
/// The file is included in <c>Interfold.DatabaseBootstrap</c> as a linked
/// <c>&lt;Compile&gt;</c> item so both projects share the same source of truth without
/// forcing DatabaseBootstrap to take a project reference on Contracts (which would blow
/// the trimmed bootstrapper binary size guardrail).
/// </remarks>
#if INTERFOLD_DBBOOT_LINKED
internal
#else
public
#endif
static class SecretsStoreKeys
{
    public static readonly SecretsStoreKey OAuthGoogleClientSecret = new("oauth:google:client_secret");
    public static readonly SecretsStoreKey OAuthDiscordClientSecret = new("oauth:discord:client_secret");
    public static readonly SecretsStoreKey OAuthAppleClientSecret = new("oauth:apple:client_secret");

    public static readonly SecretsStoreKey EncryptionPepper = new("encryption:pepper");

    public static readonly SecretsStoreKey PostgresAdminUsername = new("postgres:admin_username");
    public static readonly SecretsStoreKey PostgresAdminPassword = new("postgres:admin_password");

    public static readonly SecretsStoreKey ScyllaAdminUsername = new("scylla:admin_username");
    public static readonly SecretsStoreKey ScyllaAdminPassword = new("scylla:admin_password");
    public static readonly SecretsStoreKey ScyllaContactPoints = new("scylla:contact_points");
    public static readonly SecretsStoreKey ScyllaLocalDatacenter = new("scylla:local_datacenter");
    public static readonly SecretsStoreKey ScyllaUsername = new("scylla:username");
    public static readonly SecretsStoreKey ScyllaPassword = new("scylla:password");
    public static readonly SecretsStoreKey ScyllaPort = new("scylla:port");

    public static readonly SecretsStoreKey AuthJwtRsa256PrivatePem = new("auth:jwt_rsa256_private_pem");
    public static readonly SecretsStoreKey AuthJwtEs256PrivatePem = new("auth:jwt_es256_private_pem");
    public static readonly SecretsStoreKey AuthDeepLinkSecret = new("auth:deep_link_secret");

    public static readonly SecretsStoreKey CertsLeafPfxPassword = new("certs:leaf_pfx_password");

    public static readonly SecretsStoreKey FirebaseClientAndroid = new("firebase:client:android");
    public static readonly SecretsStoreKey FirebaseClientIos = new("firebase:client:ios");
    public static readonly SecretsStoreKey FirebaseClientWeb = new("firebase:client:web");

    /// <summary>
    /// FCM v1 service-account credential (PRIVATE — authenticates the API to Google FCM).
    /// Loaded into the secrets snapshot by <c>SecretsPreBuildLoader</c> and copied onto
    /// <c>FcmConfiguration</c> by <c>FcmSecretsPostConfigure</c>; the <c>IFCMService</c> DI
    /// factory reads <c>IOptions&lt;FcmConfiguration&gt;</c> — absent row → NullFCMService
    /// fallback so deployments without Firebase silently no-op the push flow.
    /// </summary>
    public static readonly SecretsStoreKey FcmServiceAccountJson = new("fcm:service_account_json");
}
