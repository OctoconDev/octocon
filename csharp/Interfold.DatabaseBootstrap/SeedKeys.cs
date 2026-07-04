using System.Globalization;

namespace Interfold.DatabaseBootstrap;

/// <summary>
/// The well-known <c>internal.secrets</c> rows produced by a successful seed run. Listed
/// once here so the orchestrator can iterate without re-typing the schema and so both
/// transports stay in lockstep on the row set.
/// </summary>
/// <remarks>
/// Rows whose value selector returns an empty string are skipped by
/// <see cref="PostgresSeeder"/> (matches the legacy <c>string.IsNullOrEmpty</c> guard). The
/// OAuth secrets in particular are blanked out in self-hosted deployments where the operator
/// hasn't configured a federated provider.
/// </remarks>
public static class SeedKeys
{
    /// <summary>
    /// Well-known key + value selector. Selector reads its value off the seed options at
    /// orchestration time so any future field changes only require touching the options
    /// record + this list.
    /// </summary>
    public readonly record struct Entry(string Key, Func<PostgresSeedOptions, string> ValueSelector);

    private static readonly IReadOnlyList<Entry> _all =
    [
        new("oauth:google:client_secret",  o => o.GoogleOAuthClientSecret ?? string.Empty),
        new("oauth:discord:client_secret", o => o.DiscordOAuthClientSecret ?? string.Empty),
        new("oauth:apple:client_secret",   o => o.AppleOAuthClientSecret ?? string.Empty),
        new("encryption:pepper",           o => o.EncryptionPepper),
        // PostgresMigrationService reads these to build its DDL connection.
        new("postgres:admin_username",     o => o.AdminUser),
        new("postgres:admin_password",     o => o.AdminPassword),
        // ScyllaMigrationService reads `scylla:admin_*` for its keyspace-level DDL.
        new("scylla:admin_username",       o => o.ScyllaAdminUser),
        new("scylla:admin_password",       o => o.ScyllaAdminPassword),
        new("scylla:contact_points",       o => o.ScyllaContactPoints),
        new("scylla:local_datacenter",     o => o.ScyllaLocalDatacenter),
        new("scylla:username",             o => o.ScyllaAppUser),
        new("scylla:password",             o => o.ScyllaAppPassword),
        // scylla:keyspace intentionally not seeded — region identity is per-node env
        // (OCTOCON_SCYLLA_KEYSPACE) so it must come from the deployment, not a shared store row.
        new("scylla:port",                 o => o.ScyllaPort.ToString(CultureInfo.InvariantCulture)),
        // SecretsBootstrapService patches AuthenticationConfiguration with these on startup.
        // Empty values are skipped, so a self-hosted deployment that hasn't rotated yet keeps
        // working until the next bootstrap pass.
        new("auth:jwt_rsa256_private_pem", o => o.JwtRsa256PrivateKeyPem ?? string.Empty),
        new("auth:jwt_es256_private_pem",  o => o.JwtEs256PrivateKeyPem ?? string.Empty),
        new("auth:deep_link_secret",       o => o.DeepLinkSecret ?? string.Empty),
        // The leaf PFX password is fetched by a one-shot ISecretsStore lookup in Program.cs
        // before Kestrel binds. No env shadow.
        new("certs:leaf_pfx_password",     o => o.LeafPfxPassword ?? string.Empty),
        // Firebase client-init payloads (public values) — SecretsBootstrapService
        // deserialises each into the matching FirebaseClientConfiguration variant so
        // GET /api/settings/firebase-config?platform=... can hand them out. Missing rows
        // → 503 for that platform (unconfigured deployment).
        new("firebase:client:android",     o => o.FirebaseAndroidClientJson ?? string.Empty),
        new("firebase:client:ios",         o => o.FirebaseIosClientJson ?? string.Empty),
        new("firebase:client:web",         o => o.FirebaseWebClientJson ?? string.Empty),
        // FCM v1 service-account credential (PRIVATE — authenticates the API to Google
        // FCM). Read directly by the IFCMService DI factory in ClusterServiceCollectionExtensions;
        // absent row → NullFCMService fallback so deployments without Firebase silently
        // no-op the push flow.
        new("fcm:service_account_json",    o => o.FcmServiceAccountJson ?? string.Empty),
    ];

    /// <summary>All well-known keys with their selectors, in seed order.</summary>
    public static IReadOnlyList<Entry> All => _all;
}
