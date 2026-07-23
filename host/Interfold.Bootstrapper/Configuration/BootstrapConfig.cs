using System.Text.Json.Serialization;
using Interfold.Contracts.Enums;

namespace Interfold.Bootstrapper.Configuration;

/// <summary>Operator-supplied configuration for <c>interfold.bootstrap.json</c>. Holds
/// everything that cannot be generated automatically (hosts, OAuth secrets, ports, etc.).</summary>
public sealed class BootstrapConfig
{
    [JsonPropertyName("deployment")]
    public DeploymentSection Deployment { get; set; } = new();

    [JsonPropertyName("ports")]
    public PortsSection Ports { get; set; } = new();

    /// <summary>Database stack: <c>single</c> (one Scylla node), <c>multi</c> (7-region
    /// cluster), or <c>cassandra</c>. Translated by PublishPhase into the orthogonal
    /// AppHost <c>include-scylla</c> / <c>include-cassandra</c> / <c>scylla-topology</c>.</summary>
    [JsonPropertyName("databaseMode")]
    public DatabaseMode DatabaseMode { get; set; } = DatabaseMode.Single;

    /// <summary>Pre-built API image reference. The bootstrapper does not build from source.</summary>
    [JsonPropertyName("apiImage")]
    public string ApiImage { get; set; } = "ghcr.io/azyyyyyy/interfold-api:latest";

    /// <summary>Postgres application database name. Must satisfy Postgres' 63-byte
    /// NAMEDATALEN budget and match <c>[A-Za-z_][A-Za-z0-9_]*</c>.</summary>
    [JsonPropertyName("postgresDatabase")]
    public string PostgresDatabase { get; set; } = "interfold";

    /// <summary>Cluster identity advertised by both CQL backends. Pure metadata — no
    /// keyspace/table contract attached. Validation rejects characters that would break
    /// Scylla's argv or Cassandra's cassandra.yaml rewrite (quotes, newlines, control chars).</summary>
    [JsonPropertyName("clusterName")]
    public string ClusterName { get; set; } = "InterfoldCluster";

    /// <summary>Per-instance region / keyspace identity. Also drives new-account routing.
    /// In multi mode operators pick which of the seven regional keyspaces this stack serves.</summary>
    [JsonPropertyName("scyllaKeyspace")]
    public ScyllaKeyspace ScyllaKeyspace { get; set; } = ScyllaKeyspace.Nam;

    [JsonPropertyName("apiRuntime")]
    public ApiRuntimeSection ApiRuntime { get; set; } = new();

    /// <summary>DB retry + fan-out tuning. Defaults match the API's compile-time
    /// fallbacks so a fresh bootstrap ships identical behaviour to unset env vars.</summary>
    [JsonPropertyName("persistence")]
    public PersistenceTuningSection Persistence { get; set; } = new();

    /// <summary>Node role / process-group identity. Single-field section today; kept
    /// separate so future cluster-shaped knobs have a home.</summary>
    [JsonPropertyName("cluster")]
    public ClusterSection Cluster { get; set; } = new();

    /// <summary>Avatar storage paths. Both empty → feature disabled (API binder normalises
    /// empty → null so the not-configured branch still fires).</summary>
    [JsonPropertyName("storage")]
    public StorageSection Storage { get; set; } = new();

    /// <summary>OTLP exporter configuration. Empty → API skips exporter registration.</summary>
    [JsonPropertyName("observability")]
    public ObservabilitySection Observability { get; set; } = new();

    /// <summary>WebSocket batching. Null → API's compile-time default; JSON persists
    /// literal <c>null</c> so a re-bootstrap doesn't set the threshold to 0.</summary>
    [JsonPropertyName("socket")]
    public SocketSection Socket { get; set; } = new();

    [JsonPropertyName("oauth")]
    public OAuthSection OAuth { get; set; } = new();

    /// <summary>Backup + autostart preferences for the <c>backup</c> / <c>install-service</c>
    /// subcommands. Defaults are "do nothing automatically".</summary>
    [JsonPropertyName("backup")]
    public BackupSection Backup { get; set; } = new();

    /// <summary>Image-update preferences for the <c>update-images</c> subcommand. Defaults
    /// are "manual only" — the CLI subcommand always works, but the backup → update systemd
    /// chain requires <see cref="UpdateSection.Enabled"/> + re-running install-service.</summary>
    [JsonPropertyName("update")]
    public UpdateSection Update { get; set; } = new();

    /// <summary>Firebase / FCM inputs. Empty path = skip that platform (matching
    /// <c>internal.secrets</c> row stays absent → API 503s the platform's config endpoint
    /// or falls back to <c>NullFCMService</c> for send).</summary>
    [JsonPropertyName("firebase")]
    public FirebaseSection Firebase { get; set; } = new();
}

/// <summary>Non-secret API runtime config. Every field maps 1:1 to an <c>OCTOCON_*</c>
/// env var; three of the four are derivable from <see cref="DeploymentSection"/> +
/// <see cref="PortsSection"/> — <see cref="Phases.ConfigPhase.ResolveDerivedDefaults"/>
/// fills empties, operator-supplied values win.</summary>
public sealed class ApiRuntimeSection
{
    /// <summary>OAuth callback base URL (<c>OCTOCON_AUTH_CALLBACK_BASE_URL</c>). Derived
    /// as <c>https://{primary host}[:{Ports.apiHttps}]</c> when empty — always https
    /// because the API's Kestrel binds the leaf PFX unconditionally.</summary>
    [JsonPropertyName("callbackBaseUrl")]
    public string CallbackBaseUrl { get; set; } = string.Empty;

    /// <summary>JWT <c>iss</c> claim (<c>OCTOCON_JWT_AUTHORITY</c>). Derived the same
    /// as <see cref="CallbackBaseUrl"/> — the API is usually its own issuer.</summary>
    [JsonPropertyName("jwtAuthority")]
    public string JwtAuthority { get; set; } = string.Empty;

    /// <summary>JWT <c>aud</c> claim (<c>OCTOCON_JWT_AUDIENCE</c>). Defaults to <c>octocon</c>
    /// to match the API's compile-time fallback.</summary>
    [JsonPropertyName("jwtAudience")]
    public string JwtAudience { get; set; } = "octocon";

    /// <summary>CORS allow-list (<c>OCTOCON_CORS_ALLOWED_ORIGINS</c>, comma-joined).
    /// Empty falls back to "allow any origin" in the API — production foot-gun; derived
    /// per non-CIDR host with the appropriate {scheme}://{host}[:port] when unset.</summary>
    [JsonPropertyName("corsAllowedOrigins")]
    public List<string> CorsAllowedOrigins { get; set; } = [];
}

public sealed class DeploymentSection
{
    [JsonPropertyName("outputDir")]
    public string OutputDir { get; set; } = "./deploy";

    /// <summary>Hosts the deployed API will be reachable at. Each entry is a DNS name
    /// (wildcards allowed, collapse to their suffix in the root CA name constraints),
    /// an IPv4/IPv6 literal, or a CIDR block (CIDR restricts permittedSubtrees but is
    /// not eligible as the primary host and does not land in the leaf SAN). Default is
    /// intentionally empty — non-interactive bootstrap fails fast rather than issue a
    /// cert for a placeholder. Interactive mode pre-fills via
    /// <see cref="LocalAddressDetector.TryDetectPrimaryIp"/>.</summary>
    [JsonPropertyName("hosts")]
    public List<string> Hosts { get; set; } = [];

    [JsonPropertyName("rootCaName")]
    public string RootCaName { get; set; } = "Interfold Root CA";

    [JsonPropertyName("certYears")]
    public int CertYears { get; set; } = 5;

    [JsonPropertyName("trustStoreInstall")]
    public bool TrustStoreInstall { get; set; } = true;

    /// <summary>Ship the <c>octocon-web</c> wasm UI container. Independent of
    /// <see cref="WebHttps"/> — HTTP-only is valid (external TLS terminator). Setting
    /// <see cref="WebHttps"/> forces the container in regardless.</summary>
    [JsonPropertyName("includeWeb")]
    public bool IncludeWeb { get; set; } = false;

    /// <summary>Terminate HTTPS at <c>octocon-web</c> (nginx envsubst template + leaf
    /// PFX bind-mount). Setting true implies <see cref="IncludeWeb"/> — TLS termination
    /// requires the container that performs it.</summary>
    [JsonPropertyName("webHttps")]
    public bool WebHttps { get; set; } = false;
}

public sealed class PortsSection
{
    [JsonPropertyName("apiHttp")]
    public int ApiHttp { get; set; } = 5000;

    [JsonPropertyName("apiHttps")]
    public int ApiHttps { get; set; } = 5001;

    [JsonPropertyName("webHttp")]
    public int WebHttp { get; set; } = 8080;

    [JsonPropertyName("webHttps")]
    public int WebHttps { get; set; } = 8081;

    /// <summary>Host port for Postgres. AppHost maps this to <c>Ports:postgres</c> and
    /// the derived API connection string.</summary>
    [JsonPropertyName("postgres")]
    public int Postgres { get; set; } = 4200;

    /// <summary>Host port for the CQL backend (Scylla or lone Cassandra).</summary>
    [JsonPropertyName("scylla")]
    public int Scylla { get; set; } = 9042;
}

/// <summary>Per-provider OAuth credentials. Blank client ID disables the provider even
/// if a secret is set (the scheme is only registered when the ID is non-empty).</summary>
public sealed class OAuthSection
{
    [JsonPropertyName("googleClientId")]
    public string GoogleClientId { get; set; } = string.Empty;

    [JsonPropertyName("googleClientSecret")]
    public string GoogleClientSecret { get; set; } = string.Empty;

    [JsonPropertyName("discordClientId")]
    public string DiscordClientId { get; set; } = string.Empty;

    [JsonPropertyName("discordClientSecret")]
    public string DiscordClientSecret { get; set; } = string.Empty;

    [JsonPropertyName("appleClientId")]
    public string AppleClientId { get; set; } = string.Empty;

    /// <summary>Apple's client secret is a short-lived JWT derived from the team key —
    /// operators pre-minting one outside the bootstrapper paste it here.</summary>
    [JsonPropertyName("appleClientSecret")]
    public string AppleClientSecret { get; set; } = string.Empty;
}

/// <summary>DB retry + fan-out tuning. Bounds enforced by
/// <see cref="Phases.ConfigPhase.Validate"/>.</summary>
public sealed class PersistenceTuningSection
{
    /// <summary><c>OCTOCON_DB_RETRY_ATTEMPTS</c>. Bounded 1..100 (0 would silently disable retries).</summary>
    [JsonPropertyName("dbRetryAttempts")]
    public int DbRetryAttempts { get; set; } = 3;

    /// <summary><c>OCTOCON_DB_RETRY_INITIAL_DELAY_MS</c>. Bounded 1..60000.</summary>
    [JsonPropertyName("dbRetryInitialDelayMs")]
    public int DbRetryInitialDelayMs { get; set; } = 100;

    /// <summary><c>OCTOCON_DB_RETRY_MAX_DELAY_MS</c>. Bounded 1..600000 and must be
    /// &gt;= <see cref="DbRetryInitialDelayMs"/> (Validate rejects the swap).</summary>
    [JsonPropertyName("dbRetryMaxDelayMs")]
    public int DbRetryMaxDelayMs { get; set; } = 1500;

    /// <summary><c>OCTOCON_HYDRATION_MAX_CONCURRENCY</c>. Bounded 1..1024.</summary>
    [JsonPropertyName("hydrationMaxConcurrency")]
    public int HydrationMaxConcurrency { get; set; } = 8;
}

public sealed class ClusterSection
{
    /// <summary><c>OCTOCON_NODE_GROUP</c>: <c>primary</c> / <c>auxiliary</c> / <c>sidecar</c>.
    /// Fly.io overrides via <c>FLY_PROCESS_GROUP</c> at runtime.</summary>
    [JsonPropertyName("nodeGroup")]
    public NodeGroup NodeGroup { get; set; } = NodeGroup.Auxiliary;
}

/// <summary>Local avatar storage. Both fields empty = feature disabled.</summary>
public sealed class StorageSection
{
    /// <summary>Container-side absolute path (<c>OCTOCON_AVATAR_STORAGE_ROOT</c>).
    /// Empty → API's not-configured branch. Operators are responsible for the bind mount.</summary>
    [JsonPropertyName("avatarStorageRoot")]
    public string AvatarStorageRoot { get; set; } = string.Empty;

    /// <summary>Public URL prefix (<c>OCTOCON_AVATAR_PUBLIC_BASE</c>). Non-empty must
    /// parse as an absolute http(s) URL.</summary>
    [JsonPropertyName("avatarPublicBase")]
    public string AvatarPublicBase { get; set; } = string.Empty;
}

public sealed class ObservabilitySection
{
    /// <summary>OTLP gRPC endpoint (<c>OCTOCON_OTLP_ENDPOINT</c>). Non-empty must parse
    /// as absolute http(s); other schemes are rejected.</summary>
    [JsonPropertyName("otlpEndpoint")]
    public string OtlpEndpoint { get; set; } = string.Empty;
}

public sealed class SocketSection
{
    /// <summary>Bytes threshold that flushes batched WS payloads
    /// (<c>OCTOCON_SOCKET_BATCH_BYTES_THRESHOLD</c>). Bounded 1..16777216 when set;
    /// null → API compile-time default.</summary>
    [JsonPropertyName("batchBytesThreshold")]
    public int? BatchBytesThreshold { get; set; }
}

/// <summary>Backup + autostart preferences. All fields default to "do nothing
/// automatically"; opt in explicitly then re-run <c>install-service</c>.</summary>
public sealed class BackupSection
{
    /// <summary>Master toggle for the scheduled-backup systemd timer. The one-shot
    /// <c>backup</c> subcommand always works regardless.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    /// <summary>systemd <c>OnCalendar=</c> value. Validated shallowly here;
    /// <c>systemd-analyze calendar</c> catches syntax errors at install time.</summary>
    [JsonPropertyName("schedule")]
    public string Schedule { get; set; } = "daily";

    /// <summary>Archives to keep per component. Bounded 1..1000; 0 would delete each
    /// backup immediately.</summary>
    [JsonPropertyName("retainCount")]
    public int RetainCount { get; set; } = 14;

    /// <summary>Blank → <c>{outputDir}/backups</c>. Non-empty values must be absolute —
    /// systemd-timer CWDs are unpredictable.</summary>
    [JsonPropertyName("directory")]
    public string Directory { get; set; } = string.Empty;

    /// <summary>Install & enable <c>interfold.service</c> so <c>docker compose up -d</c>
    /// runs on boot. Independent of <see cref="Enabled"/>.</summary>
    [JsonPropertyName("autostartServer")]
    public bool AutostartServer { get; set; } = false;
}

/// <summary>Image-update preferences. Defaults to "manual only" — flipping
/// <see cref="Enabled"/> and re-running install-service materialises the systemd chain
/// that fires <c>update-images</c> after each successful scheduled backup.</summary>
public sealed class UpdateSection
{
    /// <summary>Master toggle for the backup → update systemd chain. The one-shot
    /// <c>update-images</c> subcommand always works regardless.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    /// <summary>Bounded 1..3600. Ceiling on the post-<c>up -d</c> health-check wait
    /// before either printing the manual restore recipe or (with <see cref="AutoRestoreOnFailure"/>)
    /// invoking <see cref="Phases.RestorePhase"/>.</summary>
    [JsonPropertyName("healthCheckTimeoutSeconds")]
    public int HealthCheckTimeoutSeconds { get; set; } = 180;

    /// <summary>Config-side equivalent of the CLI <c>--auto-restore</c>. Default false
    /// because rollback is destructive.</summary>
    [JsonPropertyName("autoRestoreOnFailure")]
    public bool AutoRestoreOnFailure { get; set; } = false;

    /// <summary>False → <c>docker compose restart</c> instead of <c>up -d</c>. Escape
    /// hatch for two-step manual recreate; note pull-only won't run the new image until
    /// a subsequent <c>up -d</c>.</summary>
    [JsonPropertyName("recreateOnUpdate")]
    public bool RecreateOnUpdate { get; set; } = true;

    /// <summary>Compose services to pull + recreate. Empty = every service. Entries
    /// must match the AppHost's canonical service names; validator rejects typos.</summary>
    [JsonPropertyName("services")]
    public string[] Services { get; set; } = [];
}

/// <summary>Firebase inputs. Empty path = skip that platform. Paths (not inline blobs) so
/// stock deploy dirs don't accumulate multi-line secrets in <c>interfold.bootstrap.json</c> —
/// operators point at 0600-mode files under <c>secrets/firebase/</c> or wherever their
/// key-management story lives.</summary>
public sealed class FirebaseSection
{
    /// <summary>Firebase Android <c>google-services.json</c>. Reshaped into
    /// <see cref="Interfold.Contracts.Configuration.FirebaseAndroidClientConfig"/> and
    /// seeded as <c>firebase:client:android</c>.</summary>
    [JsonPropertyName("androidConfigPath")]
    public string AndroidConfigPath { get; set; } = string.Empty;

    /// <summary>iOS <c>GoogleService-Info.plist</c>. Parsed as XML and reshaped into
    /// <see cref="Interfold.Contracts.Configuration.FirebaseIosClientConfig"/> for
    /// <c>firebase:client:ios</c>.</summary>
    [JsonPropertyName("iosConfigPath")]
    public string IosConfigPath { get; set; } = string.Empty;

    /// <summary>Web-config JSON from the Firebase console (must already include the
    /// <c>vapidKey</c> from Cloud Messaging → Web Push certificates). Seeded verbatim.</summary>
    [JsonPropertyName("webConfigPath")]
    public string WebConfigPath { get; set; } = string.Empty;

    /// <summary>FCM v1 service-account credential JSON. Seeded verbatim as
    /// <c>fcm:service_account_json</c>. Protect on disk (0600). Empty → <c>NullFCMService</c>.</summary>
    [JsonPropertyName("serviceAccountPath")]
    public string ServiceAccountPath { get; set; } = string.Empty;
}
