using System.Text.Json.Serialization;
using Interfold.Contracts.Enums;

namespace Interfold.Bootstrapper.Configuration;

/// <summary>
/// Operator-supplied configuration loaded from <c>interfold.bootstrap.json</c> or built via
/// interactive prompts. Holds everything that <em>cannot</em> be generated automatically (hosts,
/// OAuth secrets, port numbers, etc.).
/// </summary>
public sealed class BootstrapConfig
{
    [JsonPropertyName("deployment")]
    public DeploymentSection Deployment { get; set; } = new();

    [JsonPropertyName("ports")]
    public PortsSection Ports { get; set; } = new();

    /// <summary>
    /// Database stack to deploy: <c>single</c> (one Scylla node), <c>multi</c> (7-region
    /// regional Scylla cluster), or <c>cassandra</c> (single Cassandra 5 node, no Scylla).
    /// PublishPhase translates this enum into the orthogonal AppHost parameters
    /// <c>include-scylla</c> / <c>include-cassandra</c> / <c>scylla-topology</c>.
    /// </summary>
    [JsonPropertyName("databaseMode")]
    public DatabaseMode DatabaseMode { get; set; } = DatabaseMode.Single;

    /// <summary>
    /// Pre-built Interfold API container image reference. The bootstrapper does NOT build the API
    /// from source - operators are expected to publish the API image to a registry and reference it
    /// here, then the generated docker-compose pulls/uses that image directly. Defaults to the
    /// canonical GHCR tag but can be overridden for testing or private registries.
    /// </summary>
    [JsonPropertyName("apiImage")]
    public string ApiImage { get; set; } = "ghcr.io/azyyyyyy/interfold-api:latest";

    /// <summary>
    /// Name of the Postgres application database that <see cref="Phases.DatabaseInitPhase"/> creates
    /// and the API connects to. Defaults to <c>interfold</c>. Operators on a shared cluster (or who
    /// want a brand-specific name) can set this to any safe Postgres identifier; the value is
    /// validated by <see cref="Phases.ConfigPhase.Validate"/>. The seeder injects it into
    /// <c>CREATE DATABASE "{value}"</c> via <see cref="DatabaseBootstrap.PostgresSqlTemplates"/>, so
    /// it must satisfy Postgres' 63-byte NAMEDATALEN budget and contain only
    /// <c>[A-Za-z_][A-Za-z0-9_]*</c>.
    /// </summary>
    [JsonPropertyName("postgresDatabase")]
    public string PostgresDatabase { get; set; } = "interfold";

    /// <summary>
    /// Cluster identity advertised by both the ScyllaDB and Cassandra backends. Defaults to
    /// <c>InterfoldCluster</c>. The value lands on Cassandra's <c>CASSANDRA_CLUSTER_NAME</c> env
    /// var (read by the entrypoint into <c>cassandra.yaml</c>) and on Scylla's
    /// <c>--cluster-name</c> CLI flag, and is what <c>SELECT cluster_name FROM system.local</c>
    /// returns. Pure metadata: it does not appear in any CQL the API issues, and there is no
    /// keyspace or table naming contract attached to it. Validated by
    /// <see cref="Phases.ConfigPhase.Validate"/> to reject empty values and characters that
    /// would break either Scylla's CLI argument parsing or Cassandra's
    /// <c>cassandra.yaml</c> rewrite (single quotes, newlines, control chars).
    /// </summary>
    [JsonPropertyName("clusterName")]
    public string ClusterName { get; set; } = "InterfoldCluster";

    /// <summary>
    /// Per-instance region / keyspace identity. Controls both the Scylla session default
    /// keyspace and the region used by the API for new-account creation and query routing
    /// (see <see cref="Interfold.Contracts.Configuration.PersistenceConfiguration.ScyllaKeyspace"/>).
    /// One of: <c>nam</c>, <c>eur</c>, <c>sam</c>, <c>sas</c>, <c>eas</c>, <c>ocn</c>, <c>gdpr</c>.
    /// Defaults to <c>nam</c>, which matches the only keyspace created in <c>single</c> /
    /// <c>cassandra</c> modes. In <c>multi</c> mode the operator picks which of the seven
    /// regional keyspaces this stack's API container serves. Validated by
    /// <see cref="Phases.ConfigPhase.Validate"/>; lands on <c>OCTOCON_SCYLLA_KEYSPACE</c> on the
    /// API container via the AppHost <c>scylla-keyspace</c> parameter.
    /// </summary>
    [JsonPropertyName("scyllaKeyspace")]
    public ScyllaKeyspace ScyllaKeyspace { get; set; } = ScyllaKeyspace.Nam;

    [JsonPropertyName("apiRuntime")]
    public ApiRuntimeSection ApiRuntime { get; set; } = new();

    /// <summary>
    /// Database retry + fan-out tuning knobs sourced from
    /// <see cref="Interfold.Contracts.Configuration.PersistenceConfiguration"/>. The four
    /// fields under here have non-null defaults the API would use anyway when the
    /// matching <c>OCTOCON_*</c> env var is unset — surfacing them in
    /// <c>BootstrapConfig</c> just lets operators tweak them per-deployment without
    /// hand-editing <c>deploy/.env</c>.
    /// </summary>
    [JsonPropertyName("persistence")]
    public PersistenceTuningSection Persistence { get; set; } = new();

    /// <summary>
    /// Node role / process-group identity. Single field today
    /// (<see cref="Interfold.Contracts.Configuration.ClusterConfiguration.NodeGroup"/>);
    /// kept in its own section so future cluster-shaped settings have a natural home.
    /// </summary>
    [JsonPropertyName("cluster")]
    public ClusterSection Cluster { get; set; } = new();

    /// <summary>
    /// Avatar storage paths sourced from
    /// <see cref="Interfold.Contracts.Configuration.StorageConfiguration"/>. Both fields
    /// are optional — leaving either empty disables the corresponding API feature (the
    /// API binder normalises empty to null so the avatar service treats it as "not
    /// configured" rather than trying to write to <c>""</c>).
    /// </summary>
    [JsonPropertyName("storage")]
    public StorageSection Storage { get; set; } = new();

    /// <summary>
    /// Observability/telemetry exporter configuration sourced from
    /// <see cref="Interfold.Contracts.Configuration.ObservabilityConfiguration"/>. Empty
    /// means the API doesn't register an OTLP exporter at startup.
    /// </summary>
    [JsonPropertyName("observability")]
    public ObservabilitySection Observability { get; set; } = new();

    /// <summary>
    /// WebSocket batching tuning sourced from
    /// <see cref="Interfold.Contracts.Configuration.SocketConfiguration"/>. The single
    /// field is nullable — null means the API uses its compile-time default. The JSON
    /// representation persists <c>null</c> rather than <c>0</c> so a re-bootstrap of a
    /// hand-edited file doesn't accidentally set the threshold to "flush every empty
    /// payload".
    /// </summary>
    [JsonPropertyName("socket")]
    public SocketSection Socket { get; set; } = new();

    [JsonPropertyName("oauth")]
    public OAuthSection OAuth { get; set; } = new();

    /// <summary>
    /// Per-host backup + autostart preferences. Drives the <c>backup</c> and
    /// <c>install-service</c> subcommands rendered by
    /// <see cref="Phases.BackupPhase"/> and <see cref="Phases.SystemdInstallPhase"/>.
    /// All fields default to a "do nothing automatically" stance so a stock bootstrap
    /// behaves identically to pre-feature deployments; operators opt in by flipping
    /// <see cref="BackupSection.Enabled"/> or <see cref="BackupSection.AutostartServer"/>
    /// and re-running <c>install-service</c>.
    /// </summary>
    [JsonPropertyName("backup")]
    public BackupSection Backup { get; set; } = new();

    /// <summary>
    /// Image-update preferences consumed by the <c>update-images</c> subcommand and by
    /// the systemd chaining that <see cref="Phases.SystemdInstallPhase"/> wires into
    /// <c>interfold-backup.service</c>'s <c>OnSuccess=</c> hook. All fields default to a
    /// "manual only" stance: <c>update-images</c> always works from the CLI regardless,
    /// but <see cref="UpdateSection.Enabled"/> must be flipped (and <c>install-service</c>
    /// re-run) before the backup timer will chain into an update after each scheduled
    /// backup.
    /// </summary>
    [JsonPropertyName("update")]
    public UpdateSection Update { get; set; } = new();

    /// <summary>
    /// Firebase / FCM inputs consumed by <see cref="Phases.FirebasePhase"/>. All four
    /// paths default to <c>""</c> — an empty path means "skip this platform / feature"
    /// and the matching <c>internal.secrets</c> row stays absent, which downstream
    /// translates to a 503 on <c>/api/settings/firebase-config</c> for the affected
    /// platform and to the <c>NullFCMService</c> fallback on the send side. Operators
    /// wire Firebase in by pointing these at the files the Firebase console hands out
    /// (<c>google-services.json</c>, <c>GoogleService-Info.plist</c>,
    /// <c>firebase-web-config.json</c>) plus the FCM v1 service-account JSON downloaded
    /// from Google Cloud IAM.
    /// </summary>
    [JsonPropertyName("firebase")]
    public FirebaseSection Firebase { get; set; } = new();
}

/// <summary>
/// Runtime config the API container needs at startup that isn't a secret and isn't a port.
/// Every field maps 1:1 to an <c>OCTOCON_*</c> env var the API consumes (see
/// <see cref="Interfold.Contracts.Configuration.AuthenticationConfiguration"/> and the CORS
/// startup block in <c>Program.cs</c>). Three of the four are deliberately derivable from
/// <see cref="DeploymentSection"/> + <see cref="PortsSection"/> so a fresh bootstrap doesn't
/// require the operator to type them: <see cref="Phases.ConfigPhase.ResolveDerivedDefaults"/>
/// fills empties with values computed from <see cref="DeploymentSection.Hosts"/>,
/// <see cref="DeploymentSection.WebHttps"/>, and the matching port entries on
/// <see cref="PortsSection"/>. Operators that want different values type them in the
/// interactive form (or set them in the JSON for non-interactive runs) and the stored value
/// wins over the derived one.
/// </summary>
public sealed class ApiRuntimeSection
{
    /// <summary>
    /// Base URL the API's OAuth callback handlers redirect to after completing the provider
    /// handshake. Lands on <c>OCTOCON_AUTH_CALLBACK_BASE_URL</c>; bound into
    /// <see cref="Interfold.Contracts.Configuration.AuthenticationConfiguration.CallbackBaseUrl"/>.
    /// When empty, derived as <c>https://{primary host}[:{Ports.apiHttps}]</c> — scheme is
    /// always <c>https</c> because the API container's Kestrel default endpoint binds to the
    /// bootstrapper-issued leaf PFX unconditionally in self-host (independent of
    /// <see cref="DeploymentSection.WebHttps"/>, which only governs the <em>web</em>
    /// container), and the port suffix is dropped when <see cref="PortsSection.ApiHttps"/>
    /// is 443. <c>primary host</c> is the first non-CIDR entry in
    /// <see cref="DeploymentSection.Hosts"/> (IPv6 literals are bracket-wrapped per RFC 3986
    /// §3.2.2).
    /// </summary>
    [JsonPropertyName("callbackBaseUrl")]
    public string CallbackBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// JWT <c>iss</c> claim the API signs into issued tokens and validates inbound tokens
    /// against. Lands on <c>OCTOCON_JWT_AUTHORITY</c>; bound into
    /// <see cref="Interfold.Contracts.Configuration.AuthenticationConfiguration.JwtAuthority"/>.
    /// When empty, derived as <c>https://{primary host}[:{Ports.apiHttps}]</c> (same as
    /// <see cref="CallbackBaseUrl"/>) — operator usually wants "this API is its own issuer".
    /// </summary>
    [JsonPropertyName("jwtAuthority")]
    public string JwtAuthority { get; set; } = string.Empty;

    /// <summary>
    /// JWT <c>aud</c> claim. Lands on <c>OCTOCON_JWT_AUDIENCE</c>; bound into
    /// <see cref="Interfold.Contracts.Configuration.AuthenticationConfiguration.JwtAudience"/>.
    /// Defaults to <c>octocon</c> to match the API's compile-time fallback and the long-standing
    /// audience name. Operators on a multi-tenant cluster can rename to disambiguate.
    /// </summary>
    [JsonPropertyName("jwtAudience")]
    public string JwtAudience { get; set; } = "octocon";

    /// <summary>
    /// Allow-list of origins the API's CORS middleware accepts cross-origin requests from.
    /// Lands on <c>OCTOCON_CORS_ALLOWED_ORIGINS</c> as a comma-separated string. When empty,
    /// derived as one entry per non-CIDR <see cref="DeploymentSection.Hosts"/> entry of the
    /// form <c>{webScheme}://{host}[:{webPort}]</c> where <c>webScheme</c> follows
    /// <see cref="DeploymentSection.WebHttps"/> and <c>webPort</c> is the matching
    /// <see cref="PortsSection.WebHttps"/> (or <see cref="PortsSection.WebHttp"/>) — the port
    /// suffix is dropped when the operator picked the scheme's default port (80 for http,
    /// 443 for https). IPv6 literals are bracket-wrapped per RFC 3986 §3.2.2. Production
    /// stacks should always have a non-empty list — an empty / unset value falls back to
    /// "allow any origin" in the API startup block, which is fine for solo dev but a
    /// foot-gun in production.
    /// </summary>
    [JsonPropertyName("corsAllowedOrigins")]
    public List<string> CorsAllowedOrigins { get; set; } = [];
}

public sealed class DeploymentSection
{
    [JsonPropertyName("outputDir")]
    public string OutputDir { get; set; } = "./deploy";

    /// <summary>
    /// Hosts the deployed API will be reachable at. Each entry is one of:
    /// <list type="bullet">
    ///   <item>A DNS name (<c>api.example.com</c>; wildcards like <c>*.example.com</c>
    ///         are allowed and collapse to their suffix in the root CA Name Constraints).</item>
    ///   <item>An IPv4 literal (<c>192.168.1.42</c>) or IPv6 literal (<c>fe80::1</c>;
    ///         brackets are tolerated on input but not stored).</item>
    ///   <item>A CIDR block (<c>192.168.1.0/24</c>, <c>fe80::/64</c>) — restricts the root
    ///         CA's Name Constraints permittedSubtrees but does <em>not</em> appear in the
    ///         leaf cert SAN and is ineligible to be the URL primary host.</item>
    /// </list>
    /// The default is intentionally empty: a non-interactive bootstrap fails fast in
    /// <see cref="Phases.ConfigPhase.Validate"/> unless the operator populates this list,
    /// so we never silently issue a cert for a placeholder. The first non-CIDR entry is
    /// the "primary host" used for the leaf cert subject CN, nginx <c>server_name</c>, and
    /// the derived <c>callbackBaseUrl</c> / <c>jwtAuthority</c> URLs — see
    /// <see cref="Phases.ConfigPhase.ResolveDerivedDefaults"/>.
    /// <para>
    /// The <em>interactive</em> bootstrap path is more forgiving: when the form starts with
    /// an empty <c>Hosts</c> list, <see cref="Phases.ConfigPhase.PromptForConfig"/> consults
    /// <see cref="LocalAddressDetector.TryDetectPrimaryIp"/> and pre-fills the row with the
    /// device's primary unicast IP (skipping loopback, tunnels, link-local, APIPA, and
    /// virtual-bridge interfaces). The operator can accept or override. The detector is
    /// never consulted on the non-interactive / JSON-loaded path — the fail-fast contract
    /// above stays intact for unattended runs.
    /// </para>
    /// </summary>
    [JsonPropertyName("hosts")]
    public List<string> Hosts { get; set; } = [];

    [JsonPropertyName("rootCaName")]
    public string RootCaName { get; set; } = "Interfold Root CA";

    [JsonPropertyName("certYears")]
    public int CertYears { get; set; } = 5;

    [JsonPropertyName("trustStoreInstall")]
    public bool TrustStoreInstall { get; set; } = true;

    /// <summary>
    /// Whether the generated compose stack includes the <c>octocon-web</c> (Kotlin/Wasm UI)
    /// container at all. Defaults to <c>false</c> so a stock bootstrap ships an API-only stack;
    /// operators who want the wasm UI shipped alongside flip this to <c>true</c>. Independent
    /// from <see cref="WebHttps"/> — set <c>includeWeb=true</c> with <c>webHttps=false</c> to
    /// ship the wasm container in HTTP-only mode (useful for local debugging and for stacks
    /// fronted by an external TLS-terminating proxy). Setting <c>webHttps=true</c>
    /// automatically pulls the container in even when this toggle is left at its default,
    /// because TLS termination is meaningless without the container that terminates it.
    /// </summary>
    [JsonPropertyName("includeWeb")]
    public bool IncludeWeb { get; set; } = false;

    /// <summary>
    /// Opt-in HTTPS termination for the <c>octocon-web</c> container. When <c>true</c>, the
    /// generated compose:
    /// <list type="bullet">
    ///   <item>Forces the <c>octocon-web</c> service into the output even when
    ///         <see cref="IncludeWeb"/> is <c>false</c> — TLS termination requires the
    ///         container that performs it.</item>
    ///   <item>Bind-mounts the bootstrapper-issued <c>certs/</c> directory into the container at
    ///         <c>/certs</c> (read-only) so nginx can read <c>leaf.crt</c> and <c>leaf.key</c>.</item>
    ///   <item>Bind-mounts a generated nginx template into
    ///         <c>/etc/nginx/templates/default.conf.template</c> so the official
    ///         <c>nginx</c> image's envsubst step renders the TLS server block at startup.</item>
    /// </list>
    /// Defaults to <c>false</c> to preserve the existing HTTP-only behaviour for operators
    /// that don't want TLS terminated at the web tier (e.g. those fronting compose with an
    /// external load balancer that handles TLS).
    /// </summary>
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

    /// <summary>
    /// Host port the Postgres / TimescaleDB container binds (compose-side). The AppHost graph
    /// reads this from <c>Ports:postgres</c> and the matching connection-string that the API
    /// consumes is derived from it. Defaults to 4200 to match the upstream AppHost configuration
    /// and keep existing operator configs working unchanged. Tests can override the value to
    /// run multiple compose stacks in parallel inside the same Docker host.
    /// </summary>
    [JsonPropertyName("postgres")]
    public int Postgres { get; set; } = 4200;

    /// <summary>
    /// Host port the Scylla / Cassandra container binds (compose-side). The AppHost graph reads
    /// this from <c>Ports:scylla</c> for both Scylla and the lone-Cassandra fallback (see
    /// <c>InterfoldAppHost.Configure</c>). Defaults to 9042 to match the upstream AppHost and
    /// the long-standing CQL convention; tests can override it for parallel compose stacks.
    /// </summary>
    [JsonPropertyName("scylla")]
    public int Scylla { get; set; } = 9042;
}

/// <summary>
/// Per-provider OAuth credentials. Each provider needs <em>both</em> the public client ID
/// (rendered into the OAuth challenge redirect URL, surfaced via <c>OCTOCON_*_OAUTH_CLIENT_ID</c>
/// env vars on the API container by <see cref="Phases.PublishPhase"/>) and the matching client
/// secret (seeded into <c>internal.secrets</c> by <see cref="Phases.DatabaseInitPhase"/> and
/// patched onto <c>AuthenticationConfiguration</c> by <c>SecretsBootstrapService</c> at API
/// startup). The scheme is only registered when the client ID is non-empty, so leaving the
/// ID blank for a provider disables that provider entirely — even if a secret is set.
/// Properties are ordered in id-then-secret pairs to mirror how providers issue them.
/// </summary>
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

    /// <summary>
    /// Apple Sign-In client secret. In Apple's flow this is a short-lived JWT derived from
    /// the team's private key — operators that pre-mint one outside the bootstrapper paste
    /// it here. Leave empty to skip the seed row (matches the Google/Discord pattern).
    /// </summary>
    [JsonPropertyName("appleClientSecret")]
    public string AppleClientSecret { get; set; } = string.Empty;
}

/// <summary>
/// Database retry strategy + per-request fan-out cap. Mirrors the four tuning fields
/// on <see cref="Interfold.Contracts.Configuration.PersistenceConfiguration"/>. All four
/// have non-null defaults that match the API's compile-time fallbacks, so a fresh
/// bootstrap with no operator input ships an API container that behaves identically to
/// one with the env vars unset — operators only need to touch these when they want to
/// deviate from the defaults.
/// </summary>
public sealed class PersistenceTuningSection
{
    /// <summary>
    /// Maximum retry attempts for transient database failures. Lands on
    /// <c>OCTOCON_DB_RETRY_ATTEMPTS</c>. Bounded 1..100 by <see cref="Phases.ConfigPhase.Validate"/>
    /// — anything below 1 disables the retry entirely (better to surface as the API's null /
    /// hard-fail path than wire up an "infinite-retry" 0).
    /// </summary>
    [JsonPropertyName("dbRetryAttempts")]
    public int DbRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Initial backoff delay (ms) for the exponential retry strategy. Lands on
    /// <c>OCTOCON_DB_RETRY_INITIAL_DELAY_MS</c>. Bounded 1..60000 — a one-minute initial
    /// delay is already long enough to be wrong for almost any deployment.
    /// </summary>
    [JsonPropertyName("dbRetryInitialDelayMs")]
    public int DbRetryInitialDelayMs { get; set; } = 100;

    /// <summary>
    /// Maximum backoff delay (ms) the exponential retry strategy will scale up to. Lands
    /// on <c>OCTOCON_DB_RETRY_MAX_DELAY_MS</c>. Bounded 1..600000 (ten minutes) and must
    /// be at least <see cref="DbRetryInitialDelayMs"/> — Validate rejects max &lt; initial
    /// so the operator catches the swap at bootstrap time rather than the first retry.
    /// </summary>
    [JsonPropertyName("dbRetryMaxDelayMs")]
    public int DbRetryMaxDelayMs { get; set; } = 1500;

    /// <summary>
    /// Maximum concurrent hydration tasks per request (caps fan-out in friendship query
    /// paths). Lands on <c>OCTOCON_HYDRATION_MAX_CONCURRENCY</c>. Bounded 1..1024 — the
    /// upper bound is sized to handle the largest realistic friendship list while still
    /// rejecting clearly-bogus values that would saturate the connection pool.
    /// </summary>
    [JsonPropertyName("hydrationMaxConcurrency")]
    public int HydrationMaxConcurrency { get; set; } = 8;
}

/// <summary>
/// Node group / process role. Single-field section today; future cluster-shaped settings
/// (replica count hints, region affinity overrides, …) would land here.
/// </summary>
public sealed class ClusterSection
{
    /// <summary>
    /// Node role used by the API for orchestration-aware decisions. Lands on
    /// <c>OCTOCON_NODE_GROUP</c>; bound into
    /// <see cref="Interfold.Contracts.Configuration.ClusterConfiguration.NodeGroup"/>
    /// (which lower-cases the value on read). Must be one of <c>primary</c> /
    /// <c>auxiliary</c> / <c>sidecar</c>; the API treats anything else as the default
    /// <c>auxiliary</c> fallback so the validator catches typos upfront. Fly.io
    /// deployments override this via <c>FLY_PROCESS_GROUP</c> at runtime.
    /// </summary>
    [JsonPropertyName("nodeGroup")]
    public NodeGroup NodeGroup { get; set; } = NodeGroup.Auxiliary;
}

/// <summary>
/// Local avatar storage configuration. Mirrors
/// <see cref="Interfold.Contracts.Configuration.StorageConfiguration"/>. Both fields are
/// optional — when both are empty, the API's avatar upload/serve endpoints behave as
/// "not configured" (returning 404 / 501 as appropriate). When the operator opts in,
/// both must be set together: an in-container filesystem path plus the public URL prefix
/// the API stitches into avatar responses.
/// </summary>
public sealed class StorageSection
{
    /// <summary>
    /// Container-side absolute path the API writes uploaded avatars to. Lands on
    /// <c>OCTOCON_AVATAR_STORAGE_ROOT</c>. Empty string means "no avatar storage
    /// configured" — the API binder normalises empty to null so the avatar service's
    /// not-configured check still works the way it did before the bootstrapper started
    /// always emitting this env var. Operators that opt in are responsible for adding
    /// the matching bind mount in a compose override; the bootstrapper does not create
    /// the directory or wire the mount itself.
    /// </summary>
    [JsonPropertyName("avatarStorageRoot")]
    public string AvatarStorageRoot { get; set; } = string.Empty;

    /// <summary>
    /// Public URL prefix the API uses to construct avatar URLs in responses (e.g.
    /// <c>https://cdn.example.com/avatars/</c>). Lands on
    /// <c>OCTOCON_AVATAR_PUBLIC_BASE</c>. Empty disables; non-empty values must parse
    /// as absolute http(s) URLs.
    /// </summary>
    [JsonPropertyName("avatarPublicBase")]
    public string AvatarPublicBase { get; set; } = string.Empty;
}

/// <summary>
/// Telemetry exporter configuration. Mirrors
/// <see cref="Interfold.Contracts.Configuration.ObservabilityConfiguration"/>. Single
/// field today; future OTLP-shaped settings (resource attributes overrides, custom
/// exporters, …) would land here.
/// </summary>
public sealed class ObservabilitySection
{
    /// <summary>
    /// OTLP gRPC endpoint the API exports traces and metrics to (e.g.
    /// <c>http://localhost:4317</c>). Lands on <c>OCTOCON_OTLP_ENDPOINT</c>. Empty
    /// string means the API skips OTLP exporter registration entirely (in-process
    /// telemetry still works). Non-empty values must parse as absolute http(s) URIs —
    /// other schemes (file://, grpc://, etc.) are rejected upfront.
    /// </summary>
    [JsonPropertyName("otlpEndpoint")]
    public string OtlpEndpoint { get; set; } = string.Empty;
}

/// <summary>
/// WebSocket batching tuning. Mirrors
/// <see cref="Interfold.Contracts.Configuration.SocketConfiguration"/>. The single field
/// is nullable so "use the API's compile-time default" is the natural blank state — the
/// JSON stores literal <c>null</c> and the operator can clear the menu row to revert.
/// </summary>
public sealed class SocketSection
{
    /// <summary>
    /// Threshold in bytes at which the API flushes a batched WebSocket payload. Lands
    /// on <c>OCTOCON_SOCKET_BATCH_BYTES_THRESHOLD</c>. Bounded 1..16777216 (16 MiB)
    /// when set; <c>null</c> means the API uses its compile-time default. The
    /// <see cref="Interfold.Contracts.Configuration.PersistenceConfiguration"/>-style
    /// <c>TryParseInt</c> binder treats empty/missing env vars as null, so the
    /// bootstrapper emits an empty string when this is null and the API's behaviour is
    /// preserved.
    /// </summary>
    [JsonPropertyName("batchBytesThreshold")]
    public int? BatchBytesThreshold { get; set; }
}

/// <summary>
/// Backup + autostart preferences consumed by the <c>backup</c> and <c>install-service</c>
/// subcommands. Every field defaults to a "do nothing automatically" stance so a stock
/// bootstrap behaves identically to pre-feature deployments — operators opt in explicitly
/// in <c>interfold.bootstrap.json</c> or via the interactive prompt, then re-run
/// <c>install-service</c> to materialise the matching systemd units.
/// </summary>
public sealed class BackupSection
{
    /// <summary>
    /// Master toggle for the scheduled-backup feature. When <c>false</c> the bootstrapper
    /// neither installs <c>interfold-backup.timer</c> nor enables it. The one-shot
    /// <c>backup</c> subcommand always works regardless of this flag — this only governs
    /// the systemd timer wiring.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Schedule expression rendered into the timer unit's <c>OnCalendar=</c> directive.
    /// Accepts systemd's calendar event syntax — the well-known shortcuts (<c>hourly</c>,
    /// <c>daily</c>, <c>weekly</c>, <c>monthly</c>) plus the full <c>DOW YYYY-MM-DD HH:MM:SS</c>
    /// form (e.g. <c>"Mon..Fri 03:30"</c>). Validation in
    /// <see cref="Phases.ConfigPhase.Validate"/> only checks for non-empty + a permissive
    /// character set; <see cref="Phases.SystemdInstallPhase"/> shells out to
    /// <c>systemd-analyze calendar</c> at install time to catch syntactically-invalid values.
    /// </summary>
    [JsonPropertyName("schedule")]
    public string Schedule { get; set; } = "daily";

    /// <summary>
    /// Number of backup archives to keep per component (<c>postgres</c>, <c>scylla</c>).
    /// After a successful backup the phase deletes the oldest archives until exactly this
    /// many remain. Bounded 1..1000 by the validator; <c>0</c> would defeat the feature
    /// entirely (every backup would immediately delete itself) so the lower bound is 1.
    /// </summary>
    [JsonPropertyName("retainCount")]
    public int RetainCount { get; set; } = 14;

    /// <summary>
    /// Override for the on-disk backup root. Blank (the default) means
    /// <c>{outputDir}/backups</c>; the phase creates <c>postgres/</c> and <c>scylla/</c>
    /// subdirectories underneath. When non-empty, must be an absolute path — relative
    /// values would resolve against the bootstrapper's CWD at backup time, which on a
    /// systemd-timer-driven invocation is unpredictable.
    /// </summary>
    [JsonPropertyName("directory")]
    public string Directory { get; set; } = string.Empty;

    /// <summary>
    /// Master toggle for the boot-up autostart feature. When <c>true</c>, running
    /// <c>install-service --enable-autostart</c> installs and enables
    /// <c>interfold.service</c>, which brings the compose stack up via
    /// <c>docker compose -f {outputDir}/docker-compose.yaml up -d</c> after
    /// <c>docker.service</c> on every boot. Independent of <see cref="Enabled"/>: operators
    /// can autostart the server without scheduled backups, or vice versa.
    /// </summary>
    [JsonPropertyName("autostartServer")]
    public bool AutostartServer { get; set; } = false;
}

/// <summary>
/// Image-update preferences consumed by the <c>update-images</c> subcommand. Every field
/// defaults to a "manual only" stance so a stock bootstrap behaves identically to
/// pre-feature deployments — operators opt in explicitly by flipping
/// <see cref="Enabled"/> in <c>interfold.bootstrap.json</c> or via the interactive
/// prompt, then re-running <c>install-service</c> to materialise the systemd chaining
/// drop-in that fires an update after each successful scheduled backup.
/// </summary>
public sealed class UpdateSection
{
    /// <summary>
    /// Master toggle for the systemd chain that runs <c>update-images</c> after each
    /// successful scheduled backup. When <c>false</c>,
    /// <see cref="Phases.SystemdInstallPhase"/> writes <c>interfold-update.service</c> but
    /// does NOT install the <c>interfold-backup.service.d/50-chain-update.conf</c>
    /// drop-in — so no unattended updates ever fire. The one-shot <c>update-images</c>
    /// subcommand always works regardless of this flag.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Bound on how long <see cref="Phases.UpdateImagesPhase"/> waits after
    /// <c>docker compose up -d</c> before it declares the stack unhealthy and either
    /// prints the manual restore recipe or (with <see cref="AutoRestoreOnFailure"/>)
    /// invokes <see cref="Phases.RestorePhase"/> inline. Bounded 1..3600 by the
    /// validator; default 180s covers the typical Postgres + Scylla + API cold start
    /// on modest hardware without letting a broken image loop indefinitely.
    /// </summary>
    [JsonPropertyName("healthCheckTimeoutSeconds")]
    public int HealthCheckTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// Config-side equivalent of the CLI <c>--auto-restore</c> flag. When <c>true</c>,
    /// a failing health check after image pull triggers an automatic
    /// <see cref="Phases.RestorePhase"/> invocation against the pre-update backup archives
    /// captured in the same run. Defaults to <c>false</c> because a rollback is a
    /// destructive operation that most operators want to gate behind an explicit opt-in.
    /// </summary>
    [JsonPropertyName("autoRestoreOnFailure")]
    public bool AutoRestoreOnFailure { get; set; } = false;

    /// <summary>
    /// Controls whether <c>update-images</c> uses <c>docker compose up -d</c> (the
    /// default; compose recreates only containers whose image changed) or
    /// <c>docker compose restart</c>. Set to <c>false</c> only when an operator wants
    /// to stage pulls without recreating containers immediately — an image-change
    /// pull with <c>restart</c> alone will NOT run the new image until a subsequent
    /// <c>up -d</c>, so the flag exists purely as an escape hatch for the two-step
    /// manual recreate workflow.
    /// </summary>
    [JsonPropertyName("recreateOnUpdate")]
    public bool RecreateOnUpdate { get; set; } = true;

    /// <summary>
    /// Whitelist of compose services to pull + recreate. Empty (the default) means
    /// "every service in the compose file". Each entry must be one of the AppHost's
    /// canonical service names (<c>msg-db</c>, <c>scylla</c>/<c>scylla-nam</c>/...,
    /// <c>cassandra</c>, <c>interfold-api</c>, <c>octocon-web</c>) — the validator
    /// rejects unknown names to catch typos before they surface as
    /// "no such service" errors from docker compose.
    /// </summary>
    [JsonPropertyName("services")]
    public string[] Services { get; set; } = [];
}

/// <summary>
/// Operator-supplied paths for the four Firebase inputs. Every field is optional and
/// defaults to <c>""</c> — an empty value means "skip this platform / feature". Paths
/// are resolved relative to the bootstrapper's CWD when relative, or taken as-is when
/// absolute; <see cref="Phases.FirebasePhase"/> validates that any non-empty value
/// resolves to an existing file before parsing.
/// <para>
/// Each field is deliberately a filesystem path rather than an inline JSON blob so a
/// stock deploy directory doesn't accumulate multi-line secrets in
/// <c>interfold.bootstrap.json</c> — operators can point at files under
/// <c>secrets/firebase/</c> (or wherever their key-management story lives) that get
/// checked in with restrictive filesystem perms rather than committed to the same JSON
/// that the interactive prompt round-trips.
/// </para>
/// </summary>
public sealed class FirebaseSection
{
    /// <summary>
    /// Path to <c>google-services.json</c> downloaded from Firebase console
    /// (Project Settings → General → Your apps → Android). The phase extracts
    /// <c>client[0].api_key[0].current_key</c>, <c>client[0].client_info.mobilesdk_app_id</c>,
    /// <c>project_info.project_id</c>, <c>project_info.project_number</c>, and
    /// <c>project_info.storage_bucket</c> and emits a normalised
    /// <see cref="Interfold.Contracts.Configuration.FirebaseAndroidClientConfig"/>
    /// JSON string as the <c>firebase:client:android</c> seed row. Leave empty on
    /// stacks that don't ship an Android build.
    /// </summary>
    [JsonPropertyName("androidConfigPath")]
    public string AndroidConfigPath { get; set; } = string.Empty;

    /// <summary>
    /// Path to <c>GoogleService-Info.plist</c> downloaded from Firebase console (iOS
    /// app). Parsed via <c>System.Xml.Linq</c> and reshaped into a
    /// <see cref="Interfold.Contracts.Configuration.FirebaseIosClientConfig"/> JSON
    /// string as the <c>firebase:client:ios</c> seed row. Leave empty on stacks that
    /// don't ship an iOS build.
    /// </summary>
    [JsonPropertyName("iosConfigPath")]
    public string IosConfigPath { get; set; } = string.Empty;

    /// <summary>
    /// Path to the flat web-config JSON copy-pasted from Firebase console (Project
    /// Settings → General → Your apps → Web → SDK setup and configuration). Must
    /// match <see cref="Interfold.Contracts.Configuration.FirebaseWebClientConfig"/>
    /// 1:1 (including the operator-injected <c>vapidKey</c> from Cloud Messaging →
    /// Web configuration → Web Push certificates); the phase reads it as-is and seeds
    /// it verbatim into <c>firebase:client:web</c>. Leave empty on stacks that don't
    /// ship the wasm client.
    /// </summary>
    [JsonPropertyName("webConfigPath")]
    public string WebConfigPath { get; set; } = string.Empty;

    /// <summary>
    /// Path to the FCM v1 service-account credential JSON (Firebase console → Project
    /// Settings → Service accounts → Generate new private key). Seeded verbatim as
    /// <c>fcm:service_account_json</c>. Protect on disk with the same 0600 mode as
    /// <c>secrets.json</c> — this is a private credential that authenticates the API
    /// as the FCM project's server-side agent. Leave empty to fall back to
    /// <c>NullFCMService</c> and disable server-side push entirely.
    /// </summary>
    [JsonPropertyName("serviceAccountPath")]
    public string ServiceAccountPath { get; set; } = string.Empty;
}
