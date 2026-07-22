namespace Interfold.Contracts.Secrets;

// Typed row key for `internal.secrets`. Struct + registry share this file because it's
// linked into DatabaseBootstrap; unwrap Value at DB parameters and dictionary keys.
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

/// <summary>Well-known row keys in <c>internal.secrets</c>. Persisted wire values —
/// changing any string here is a breaking schema change for existing deployments.</summary>
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

    /// <summary>FCM v1 service-account credential (PRIVATE). Absent row → NullFCMService,
    /// so deployments without Firebase silently no-op the push flow.</summary>
    public static readonly SecretsStoreKey FcmServiceAccountJson = new("fcm:service_account_json");
}
