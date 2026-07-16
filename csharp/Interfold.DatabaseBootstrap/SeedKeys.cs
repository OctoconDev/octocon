using System.Globalization;
using Interfold.Contracts.Secrets;

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
internal static class SeedKeys
{
    /// <summary>
    /// Well-known key + value selector. Selector reads its value off the seed options at
    /// orchestration time so any future field changes only require touching the options
    /// record + this list.
    /// </summary>
    internal readonly record struct Entry(SecretsStoreKey Key, Func<PostgresSeedOptions, string> ValueSelector);

    private static readonly IReadOnlyList<Entry> _all =
    [
        new(SecretsStoreKeys.OAuthGoogleClientSecret,  o => o.GoogleOAuthClientSecret ?? string.Empty),
        new(SecretsStoreKeys.OAuthDiscordClientSecret, o => o.DiscordOAuthClientSecret ?? string.Empty),
        new(SecretsStoreKeys.OAuthAppleClientSecret,   o => o.AppleOAuthClientSecret ?? string.Empty),
        new(SecretsStoreKeys.EncryptionPepper,         o => o.EncryptionPepper),
        // PostgresMigrationService reads these to build its DDL connection.
        new(SecretsStoreKeys.PostgresAdminUsername,    o => o.AdminUser),
        new(SecretsStoreKeys.PostgresAdminPassword,    o => o.AdminPassword),
        // ScyllaMigrationService reads `scylla:admin_*` for its keyspace-level DDL.
        new(SecretsStoreKeys.ScyllaAdminUsername,      o => o.ScyllaAdminUser),
        new(SecretsStoreKeys.ScyllaAdminPassword,      o => o.ScyllaAdminPassword),
        new(SecretsStoreKeys.ScyllaContactPoints,      o => o.ScyllaContactPoints),
        new(SecretsStoreKeys.ScyllaLocalDatacenter,    o => o.ScyllaLocalDatacenter),
        new(SecretsStoreKeys.ScyllaUsername,           o => o.ScyllaAppUser),
        new(SecretsStoreKeys.ScyllaPassword,           o => o.ScyllaAppPassword),
        // scylla:keyspace intentionally not seeded — region identity is per-node env
        // (OCTOCON_SCYLLA_KEYSPACE) so it must come from the deployment, not a shared store row.
        new(SecretsStoreKeys.ScyllaPort,               o => o.ScyllaPort.ToString(CultureInfo.InvariantCulture)),
        // SecretsBootstrapService patches AuthenticationConfiguration with these on startup.
        // Empty values are skipped, so a self-hosted deployment that hasn't rotated yet keeps
        // working until the next bootstrap pass.
        new(SecretsStoreKeys.AuthJwtRsa256PrivatePem,  o => o.JwtRsa256PrivateKeyPem ?? string.Empty),
        new(SecretsStoreKeys.AuthJwtEs256PrivatePem,   o => o.JwtEs256PrivateKeyPem ?? string.Empty),
        new(SecretsStoreKeys.AuthDeepLinkSecret,       o => o.DeepLinkSecret ?? string.Empty),
        // The leaf PFX password is fetched by SecretsPreBuildLoader via a bare NpgsqlConnection
        // before builder.Build() runs, then written into IConfiguration under
        // Kestrel:Certificates:Default:Password so the framework's cert loader observes it out
        // of the frozen IConfigurationRoot. No env shadow.
        new(SecretsStoreKeys.CertsLeafPfxPassword,     o => o.LeafPfxPassword ?? string.Empty),
        // Firebase client-init payloads (public values) — SecretsBootstrapService
        // deserialises each into the matching FirebaseClientConfiguration variant so
        // GET /api/settings/firebase-config?platform=... can hand them out. Missing rows
        // → 503 for that platform (unconfigured deployment).
        new(SecretsStoreKeys.FirebaseClientAndroid,    o => o.FirebaseAndroidClientJson ?? string.Empty),
        new(SecretsStoreKeys.FirebaseClientIos,        o => o.FirebaseIosClientJson ?? string.Empty),
        new(SecretsStoreKeys.FirebaseClientWeb,        o => o.FirebaseWebClientJson ?? string.Empty),
        // FCM v1 service-account credential (PRIVATE — authenticates the API to Google
        // FCM). Loaded into the secrets snapshot by SecretsPreBuildLoader and copied onto
        // FcmConfiguration by FcmSecretsPostConfigure; the IFCMService DI factory reads
        // IOptions<FcmConfiguration> — absent row → NullFCMService fallback so deployments
        // without Firebase silently no-op the push flow.
        new(SecretsStoreKeys.FcmServiceAccountJson,    o => o.FcmServiceAccountJson ?? string.Empty),
    ];

    /// <summary>All well-known keys with their selectors, in seed order.</summary>
    internal static IReadOnlyList<Entry> All => _all;
}
