using System.Text.Json.Serialization;

namespace Interfold.Bootstrapper.Configuration;

/// <summary>
/// Auto-generated secret material. Persisted to <c>deploy/secrets/secrets.json</c> with mode 0600.
/// Treated as the single source of truth — never overwritten unless <c>--rotate-secrets</c> is passed.
/// </summary>
public sealed class GeneratedSecrets
{
    [JsonPropertyName("postgresUser")]
    public string PostgresUser { get; set; } = "interfold";

    [JsonPropertyName("postgresPassword")]
    public string PostgresPassword { get; set; } = string.Empty;

    /// <summary>
    /// Transient init/cluster-owner password for postgres. The compose file passes this to
    /// <c>msg-db</c> via <c>POSTGRES_PASSWORD</c> so initdb can create the <c>db_init</c> bootstrap
    /// superuser on the first container start. <see cref="Phases.DatabaseInitPhase"/> uses it
    /// once to mint the app + admin roles, then scrambles <c>db_init</c>'s password in-cluster -
    /// so by the time the API starts, the value sitting in <c>.env</c> is already stale.
    /// </summary>
    [JsonPropertyName("postgresInitPassword")]
    public string PostgresInitPassword { get; set; } = string.Empty;

    /// <summary>
    /// Random password for the postgres `&lt;user&gt;_admin` role. Created by DatabaseInitPhase and
    /// persisted here so a subsequent rerun can rotate the role's password to match. It is never
    /// exposed via the compose .env or via AppHost parameters - the value of record lives inside
    /// the `internal.secrets` table where the API reads it from.
    /// </summary>
    [JsonPropertyName("postgresAdminPassword")]
    public string PostgresAdminPassword { get; set; } = string.Empty;

    [JsonPropertyName("scyllaUser")]
    public string ScyllaUser { get; set; } = "interfold";

    [JsonPropertyName("scyllaPassword")]
    public string ScyllaPassword { get; set; } = string.Empty;

    /// <summary>
    /// Random password for the scylla `&lt;user&gt;_admin` superuser. Same handling as
    /// <see cref="PostgresAdminPassword"/> - kept in secrets.json on disk for idempotent reruns,
    /// surfaced inside the cluster only via internal.secrets.
    /// </summary>
    [JsonPropertyName("scyllaAdminPassword")]
    public string ScyllaAdminPassword { get; set; } = string.Empty;

    [JsonPropertyName("encryptionPrivateKeyB64")]
    public string EncryptionPrivateKeyB64 { get; set; } = string.Empty;

    [JsonPropertyName("encryptionPepper")]
    public string EncryptionPepper { get; set; } = string.Empty;

    /// <summary>
    /// HMAC signing secret for the phase-F deep-link token exchange. Seeded once and read
    /// by the API exclusively via <c>auth:deep_link_secret</c> in <c>internal.secrets</c> —
    /// never surfaced as an env var or compose parameter.
    /// </summary>
    [JsonPropertyName("deepLinkSecret")]
    public string DeepLinkSecret { get; set; } = string.Empty;

    [JsonPropertyName("leafPfxPassword")]
    public string LeafPfxPassword { get; set; } = string.Empty;

    /// <summary>
    /// RSA-2048 JWT signing keypair (public half). The API verifies inbound bearer tokens with
    /// this and rejects startup if it's absent (see <c>ApplyAuthentication</c> in
    /// <c>ConfigurationServiceCollectionExtensions</c>). Stored as PEM (SPKI) so it can be written
    /// straight to disk and bind-mounted into the container.
    /// </summary>
    [JsonPropertyName("jwtRsa256PublicKeyPem")]
    public string JwtRsa256PublicKeyPem { get; set; } = string.Empty;

    /// <summary>RSA-2048 JWT signing keypair (private half) - PEM PKCS#8.</summary>
    [JsonPropertyName("jwtRsa256PrivateKeyPem")]
    public string JwtRsa256PrivateKeyPem { get; set; } = string.Empty;

    /// <summary>ES256 JWT signing keypair (public half) - PEM SPKI.</summary>
    [JsonPropertyName("jwtEs256PublicKeyPem")]
    public string JwtEs256PublicKeyPem { get; set; } = string.Empty;

    /// <summary>ES256 JWT signing keypair (private half) - PEM SEC1.</summary>
    [JsonPropertyName("jwtEs256PrivateKeyPem")]
    public string JwtEs256PrivateKeyPem { get; set; } = string.Empty;
}
