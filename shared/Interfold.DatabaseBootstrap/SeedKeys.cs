using System.Globalization;
using Interfold.Shared.Contracts.Secrets;

namespace Interfold.DatabaseBootstrap;

/// <summary>Well-known <c>internal.secrets</c> rows produced by a successful seed run.
/// Rows whose selector returns empty are skipped by <see cref="PostgresSeeder"/>, so
/// unconfigured providers (OAuth, Firebase, FCM) leave the row absent and callers 503.</summary>
internal static class SeedKeys
{
    internal readonly record struct Entry(SecretsStoreKey Key, Func<PostgresSeedOptions, string> ValueSelector);

    private static readonly IReadOnlyList<Entry> _all =
    [
        new(SecretsStoreKeys.OAuthGoogleClientSecret,  o => o.GoogleOAuthClientSecret ?? string.Empty),
        new(SecretsStoreKeys.OAuthDiscordClientSecret, o => o.DiscordOAuthClientSecret ?? string.Empty),
        new(SecretsStoreKeys.OAuthAppleClientSecret,   o => o.AppleOAuthClientSecret ?? string.Empty),
        new(SecretsStoreKeys.EncryptionPepper,         o => o.EncryptionPepper),
        new(SecretsStoreKeys.PostgresAdminUsername,    o => o.AdminUser),
        new(SecretsStoreKeys.PostgresAdminPassword,    o => o.AdminPassword),
        new(SecretsStoreKeys.ScyllaAdminUsername,      o => o.ScyllaAdminUser),
        new(SecretsStoreKeys.ScyllaAdminPassword,      o => o.ScyllaAdminPassword),
        new(SecretsStoreKeys.ScyllaContactPoints,      o => o.ScyllaContactPoints),
        new(SecretsStoreKeys.ScyllaLocalDatacenter,    o => o.ScyllaLocalDatacenter),
        new(SecretsStoreKeys.ScyllaUsername,           o => o.ScyllaAppUser),
        new(SecretsStoreKeys.ScyllaPassword,           o => o.ScyllaAppPassword),
        // scylla:keyspace is per-node env (OCTOCON_SCYLLA_KEYSPACE), never a store row.
        new(SecretsStoreKeys.ScyllaPort,               o => o.ScyllaPort.ToString(CultureInfo.InvariantCulture)),
        new(SecretsStoreKeys.AuthJwtRsa256PrivatePem,  o => o.JwtRsa256PrivateKeyPem ?? string.Empty),
        new(SecretsStoreKeys.AuthJwtEs256PrivatePem,   o => o.JwtEs256PrivateKeyPem ?? string.Empty),
        new(SecretsStoreKeys.AuthDeepLinkSecret,       o => o.DeepLinkSecret ?? string.Empty),
        // Read pre-builder by SecretsPreBuildLoader (bare NpgsqlConnection) and written into
        // Kestrel:Certificates:Default:Password on the frozen IConfigurationRoot.
        new(SecretsStoreKeys.CertsLeafPfxPassword,     o => o.LeafPfxPassword ?? string.Empty),
        new(SecretsStoreKeys.FirebaseClientAndroid,    o => o.FirebaseAndroidClientJson ?? string.Empty),
        new(SecretsStoreKeys.FirebaseClientIos,        o => o.FirebaseIosClientJson ?? string.Empty),
        new(SecretsStoreKeys.FirebaseClientWeb,        o => o.FirebaseWebClientJson ?? string.Empty),
        // Private FCM v1 credential — absent → NullFCMService (push flow no-ops).
        new(SecretsStoreKeys.FcmServiceAccountJson,    o => o.FcmServiceAccountJson ?? string.Empty),
    ];

    internal static IReadOnlyList<Entry> All => _all;
}
