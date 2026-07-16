using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using Interfold.Contracts.Enums;
using TUnit.Core;
using Interfold.Contracts.Configuration;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Unit tests for <see cref="PublishPhase.BuildEnvReplacements"/>. The pure function returns the
/// keys the bootstrapper plans to substitute into the Aspire-emitted <c>.env</c>; the integration
/// tests confirm that those same keys are present in the <em>real</em> emitted file, but this
/// unit-level check catches drift in either direction at sub-second speed.
///
/// Key-count history: after the JWT / OAuth-secret / leaf-PFX-password migration into
/// <c>internal.secrets</c> the parameter dict shrunk from 10 to 6 keys; <c>POSTGRES_DB</c>
/// (operator-tunable application database name) bumped it back to 7; the three OAuth
/// client IDs (public per-provider identifiers paired with the secrets in
/// <c>internal.secrets</c>) brought it to 10. The five API-runtime keys
/// (<c>SCYLLA_KEYSPACE</c>, <c>OAUTH_CALLBACK_BASE_URL</c>, <c>JWT_AUTHORITY</c>,
/// <c>JWT_AUDIENCE</c>, <c>CORS_ALLOWED_ORIGINS</c>) brought it to 15. The API bind-mount
/// set dropped from two (<c>/keys</c> + <c>/certs</c>) to one (<c>/certs</c>). The nine
/// operator tuning keys (<c>NODE_GROUP</c>, two avatar paths, <c>OTLP_ENDPOINT</c>, the
/// nullable socket batch threshold, and the four DB-retry / hydration tuning ints)
/// brought the parameter dict to 24 and the single-mode total to 24 + 1 + 1 = 26.
/// </summary>
public sealed class PublishEnvPostProcessingTests
{
    private static (BootstrapConfig Config, GeneratedSecrets Secrets) MakeInputs(
        string? apiImage = null,
        DatabaseMode databaseMode = DatabaseMode.Single)
    {
        var config = new BootstrapConfig
        {
            DatabaseMode = databaseMode,
            ApiImage = apiImage ?? "ghcr.io/azyyyyyy/interfold-api:latest",
        };
        // BootstrapConfig.Deployment.Hosts has no default placeholder, so populate it explicitly
        // here. Without this the ResolveDerivedDefaults call below has nothing to seed
        // CallbackBaseUrl / JwtAuthority / CorsAllowedOrigins from, and the required-keys check
        // (which asserts post-derivation non-emptiness) would fail.
        config.Deployment.Hosts = ["api.example.com"];
        // OAuth client secrets flow through PostgresSeedOptions into internal.secrets;
        // they no longer appear in the env replacements. Setting them here only exercises
        // the irrelevant config path.
        config.OAuth.GoogleClientSecret = "google-secret-from-config";
        config.OAuth.DiscordClientSecret = "discord-secret-from-config";

        // BuildEnvReplacements assumes ConfigPhase has already run ResolveDerivedDefaults
        // (Validate runs it on every config). Mirror that here so the unit tests exercise
        // the same post-derivation snapshot the runtime sees, without depending on the
        // full Validate path (the publish tests intentionally allow exotic combinations
        // like exotic apiImage strings that Validate would reject).
        ConfigPhase.ResolveDerivedDefaults(config);
        return (config, SecretsPhase.Generate());
    }

    [Test]
    public async Task BuildEnvReplacementsProducesAllRequiredParameterKeys()
    {
        var (config, secrets) = MakeInputs();
        // Pin all three OAuth IDs so the non-empty assertion below is meaningful; the
        // empty-IDs-still-emit-keys behaviour is locked down by a dedicated test below.
        config.OAuth.GoogleClientId = "google-client-id";
        config.OAuth.DiscordClientId = "discord-client-id";
        config.OAuth.AppleClientId = "apple-client-id";
        // Same shape for the four optional / nullable tuning fields — pin non-empty
        // values here so the IsNotEmpty assertion stays meaningful for all required
        // keys; the matching empty-still-emits-key behaviour is locked down by
        // BuildEnvReplacementsEmitsEmptyTuningSlotsForNullables below.
        config.Storage.AvatarStorageRoot = "/var/lib/interfold/avatars";
        config.Storage.AvatarPublicBase = "https://cdn.example.com/avatars/";
        config.Observability.OtlpEndpoint = "http://localhost:4317";
        config.Socket.BatchBytesThreshold = 65536;
        const string baseDir = "/var/lib/interfold";
        const string outputDir = "/srv/interfold/deploy";

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, baseDir, outputDir);

        string[] required =
        [
            "POSTGRES_USER",
            "POSTGRES_PASSWORD",
            "POSTGRES_INIT_PASSWORD",
            "POSTGRES_DB",
            "SCYLLA_USER",
            "SCYLLA_PASSWORD",
            "ENCRYPTION_PRIVATE_KEY",
            // Public per-provider OAuth identifiers - paired with the per-provider secrets that
            // live in internal.secrets. The matching scheme registration on the API side is
            // gated on the client ID being non-empty; the bootstrapper writes whatever
            // BootstrapConfig.OAuth.*ClientId says, including the empty string (see
            // BuildEnvReplacementsEmitsEmptyOAuthClientIdsWhenNotConfigured below).
            "GOOGLE_OAUTH_CLIENT_ID",
            "DISCORD_OAUTH_CLIENT_ID",
            "APPLE_OAUTH_CLIENT_ID",
            // API runtime config — bootstrapper-managed, sourced from BootstrapConfig
            // (ScyllaKeyspace + ApiRuntime.*). All five are post-derivation non-empty
            // (MakeInputs runs ResolveDerivedDefaults).
            "SCYLLA_KEYSPACE",
            "OAUTH_CALLBACK_BASE_URL",
            "JWT_AUTHORITY",
            "JWT_AUDIENCE",
            "CORS_ALLOWED_ORIGINS",
            // Operator tuning knobs — five of these have non-null defaults on
            // BootstrapConfig (NodeGroup "auxiliary"; DbRetry attempts/initial/max =
            // 3/100/1500; HydrationMaxConcurrency = 8) and so always land here non-empty.
            // The four nullable-or-empty-allowed fields (Avatar* / OTLP / socket
            // threshold) are pinned non-empty above to keep the IsNotEmpty assertion
            // meaningful; their "blank still emits the key" behaviour is locked down by
            // BuildEnvReplacementsEmitsEmptyTuningSlotsForNullables below.
            "NODE_GROUP",
            "AVATAR_STORAGE_ROOT",
            "AVATAR_PUBLIC_BASE",
            "OTLP_ENDPOINT",
            "SOCKET_BATCH_BYTES_THRESHOLD",
            "DB_RETRY_ATTEMPTS",
            "DB_RETRY_INITIAL_DELAY_MS",
            "DB_RETRY_MAX_DELAY_MS",
            "HYDRATION_MAX_CONCURRENCY",
        ];
        foreach (var key in required)
        {
            await Assert.That(replacements.Parameters.ContainsKey(key)).IsTrue()
                .Because($"missing parameter key '{key}' in env replacements");
            await Assert.That(replacements.Parameters[key]).IsNotEmpty()
                .Because($"parameter '{key}' must be non-empty");
        }

        // The encryption pepper, OAuth client secrets, JWT material, deep-link secret, and
        // the leaf PFX password must NOT appear here any more — they all live inside
        // internal.secrets and are loaded at startup (SecretsBootstrapService / Program.cs
        // Kestrel loader).
        await Assert.That(replacements.Parameters.ContainsKey("ENCRYPTION_PEPPER")).IsFalse();
        await Assert.That(replacements.Parameters.ContainsKey("GOOGLE_OAUTH_CLIENT_SECRET")).IsFalse();
        await Assert.That(replacements.Parameters.ContainsKey("DISCORD_OAUTH_CLIENT_SECRET")).IsFalse();
        await Assert.That(replacements.Parameters.ContainsKey("APPLE_OAUTH_CLIENT_SECRET")).IsFalse();
        await Assert.That(replacements.Parameters.ContainsKey("LEAF_PFX_PASSWORD")).IsFalse();
        // Admin credentials must also stay inside internal.secrets exclusively.
        await Assert.That(replacements.Parameters.ContainsKey("SCYLLA_ADMIN_PASSWORD")).IsFalse();
        await Assert.That(replacements.Parameters.ContainsKey("POSTGRES_ADMIN_PASSWORD")).IsFalse();
    }

    [Test]
    public async Task BuildEnvReplacementsCarriesOAuthClientIdsFromConfig()
    {
        // Pin the contract: BuildEnvReplacements copies the operator's BootstrapConfig.OAuth.*ClientId
        // values verbatim into the env-replacement dict. Each provider's ID lands on the matching
        // OCTOCON_*_OAUTH_CLIENT_ID env var via the Parameters:*-oauth-client-id wiring in
        // PublishInProcessAsync (and the ConfigureApiSelfHostEnv WithEnvironment calls in
        // InterfoldAppHost), so a drift between BootstrapConfig field name and Aspire parameter
        // name would surface as either a missing key here or a value mismatch.
        var (config, secrets) = MakeInputs();
        config.OAuth.GoogleClientId = "google-client-from-config.apps.googleusercontent.com";
        config.OAuth.DiscordClientId = "1234567890";
        config.OAuth.AppleClientId = "com.example.interfold.signin";

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, "/base", "/out");

        await Assert.That(replacements.Parameters["GOOGLE_OAUTH_CLIENT_ID"])
            .IsEqualTo("google-client-from-config.apps.googleusercontent.com");
        await Assert.That(replacements.Parameters["DISCORD_OAUTH_CLIENT_ID"]).IsEqualTo("1234567890");
        await Assert.That(replacements.Parameters["APPLE_OAUTH_CLIENT_ID"])
            .IsEqualTo("com.example.interfold.signin");
    }

    [Test]
    public async Task BuildEnvReplacementsEmitsEmptyOAuthClientIdsWhenNotConfigured()
    {
        // Empty client IDs are a valid "I'm not using this provider" signal — the API's
        // scheme registrar skips schemes whose OCTOCON_*_OAUTH_CLIENT_ID is empty. The
        // bootstrapper must still emit explicit empty-string entries (rather than omitting
        // the keys) so the Aspire-emitted blank `.env` lines get rewritten as `KEY=`
        // instead of triggering the "blank value left unfilled" operator warning in
        // ApplyReplacementsToEnvFile.
        var (config, secrets) = MakeInputs();
        // (defaults are already empty for all three IDs)

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, "/base", "/out");

        await Assert.That(replacements.Parameters.ContainsKey("GOOGLE_OAUTH_CLIENT_ID")).IsTrue();
        await Assert.That(replacements.Parameters.ContainsKey("DISCORD_OAUTH_CLIENT_ID")).IsTrue();
        await Assert.That(replacements.Parameters.ContainsKey("APPLE_OAUTH_CLIENT_ID")).IsTrue();
        await Assert.That(replacements.Parameters["GOOGLE_OAUTH_CLIENT_ID"]).IsEqualTo(string.Empty);
        await Assert.That(replacements.Parameters["DISCORD_OAUTH_CLIENT_ID"]).IsEqualTo(string.Empty);
        await Assert.That(replacements.Parameters["APPLE_OAUTH_CLIENT_ID"]).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task BuildEnvReplacementsCarriesPostgresDatabaseNameFromConfig()
    {
        // POSTGRES_DB is operator-tunable via BootstrapConfig.PostgresDatabase; the env
        // replacement must use the configured value verbatim so the API connection string
        // (Database=<value>) lines up with the database DatabaseInitPhase actually creates.
        var (config, secrets) = MakeInputs();
        config.PostgresDatabase = "my_custom_db";

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, "/base", "/out");

        await Assert.That(replacements.Parameters.ContainsKey("POSTGRES_DB")).IsTrue();
        await Assert.That(replacements.Parameters["POSTGRES_DB"]).IsEqualTo("my_custom_db");
    }

    [Test]
    public async Task BuildEnvReplacementsProducesAllTwentySixKeysInSingleMode()
    {
        var (config, secrets) = MakeInputs(databaseMode: DatabaseMode.Single);
        const string baseDir = "/var/lib/interfold";
        const string outputDir = "/srv/interfold/deploy";

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, baseDir, outputDir);

        var total = replacements.Parameters.Count + replacements.BindMounts.Count;
        // Single mode: 24 parameter keys (7 infra + 3 OAuth client IDs + 5 API runtime
        // + 9 operator tuning: NODE_GROUP, two avatar paths, OTLP_ENDPOINT, socket
        // threshold, four DB-retry/hydration ints) + 1 API bind mount (/certs)
        // + 1 Scylla rackdc bind mount = 26.
        await Assert.That(total).IsEqualTo(26);
    }

    [Test]
    public async Task BuildEnvReplacementsCarriesApiRuntimeFromConfig()
    {
        // Pin the contract: BuildEnvReplacements copies BootstrapConfig.ScyllaKeyspace and
        // every ApiRuntime field verbatim into the env replacements. Each value lands on
        // the matching OCTOCON_* env var via Parameters:scylla-keyspace / oauth-callback-base-url
        // / jwt-authority / jwt-audience / cors-allowed-origins (see PublishInProcessAsync) and
        // the WithEnvironment calls in InterfoldAppHost.ConfigureApiSelfHostEnv.
        var (config, secrets) = MakeInputs();
        config.ScyllaKeyspace = ScyllaKeyspace.Eur;
        config.ApiRuntime.CallbackBaseUrl = "https://callback.example.com";
        config.ApiRuntime.JwtAuthority = "https://issuer.example.com";
        config.ApiRuntime.JwtAudience = "custom-aud";
        config.ApiRuntime.CorsAllowedOrigins = ["https://app.example.com", "https://admin.example.com"];

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, "/base", "/out");

        await Assert.That(replacements.Parameters["SCYLLA_KEYSPACE"]).IsEqualTo("eur");
        await Assert.That(replacements.Parameters["OAUTH_CALLBACK_BASE_URL"])
            .IsEqualTo("https://callback.example.com");
        await Assert.That(replacements.Parameters["JWT_AUTHORITY"]).IsEqualTo("https://issuer.example.com");
        await Assert.That(replacements.Parameters["JWT_AUDIENCE"]).IsEqualTo("custom-aud");
        // CorsAllowedOrigins lands on OCTOCON_CORS_ALLOWED_ORIGINS as a comma-separated string;
        // the API's CORS startup block splits on the same character.
        await Assert.That(replacements.Parameters["CORS_ALLOWED_ORIGINS"])
            .IsEqualTo("https://app.example.com,https://admin.example.com");
    }

    [Test]
    public async Task BuildEnvReplacementsDerivesApiRuntimeFromDeploymentWhenUnset()
    {
        // The bootstrapper's contract: leaving apiRuntime.* empty derives the values from
        // deployment.hosts + deployment.webHttps via ConfigPhase.ResolveDerivedDefaults
        // (which MakeInputs runs on every config). The derived defaults must round-trip
        // through BuildEnvReplacements unchanged so the .env reflects what the menu showed.
        var config = new BootstrapConfig
        {
            Deployment =
            {
                Hosts = ["api.example.com", "admin.example.com"],
                WebHttps = true,
            },
        };
        // ResolveDerivedDefaults runs explicitly here (rather than reusing MakeInputs, which
        // hands you ghcr.io defaults) so we exercise the empty-apiRuntime path.
        ConfigPhase.ResolveDerivedDefaults(config);
        var secrets = SecretsPhase.Generate();

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, "/base", "/out");

        // The API URL is always https + :{Ports.ApiHttps} in self-host because the API
        // container's Kestrel default endpoint binds to the bootstrapper-issued leaf PFX
        // unconditionally (independent of Deployment.WebHttps, which only governs the web
        // container). Default Ports.ApiHttps=5001 here.
        await Assert.That(replacements.Parameters["OAUTH_CALLBACK_BASE_URL"])
            .IsEqualTo("https://api.example.com:5001");
        await Assert.That(replacements.Parameters["JWT_AUTHORITY"])
            .IsEqualTo("https://api.example.com:5001");
        // JwtAudience has a hardcoded property-initialiser default ("octocon"); not derived.
        await Assert.That(replacements.Parameters["JWT_AUDIENCE"]).IsEqualTo("octocon");
        // CORS represents the SPA / native-client origins; scheme follows WebHttps and port
        // follows Ports.WebHttps (8081 default) — one entry per non-CIDR host.
        await Assert.That(replacements.Parameters["CORS_ALLOWED_ORIGINS"])
            .IsEqualTo("https://api.example.com:8081,https://admin.example.com:8081");
        // ScyllaKeyspace's hardcoded default lives on the field itself, not in derivation.
        await Assert.That(replacements.Parameters["SCYLLA_KEYSPACE"]).IsEqualTo("nam");
    }

    [Test]
    public async Task BuildEnvReplacementsCarriesTuningFromConfig()
    {
        // Pin the contract: every operator tuning field round-trips through
        // BuildEnvReplacements verbatim. Each lands on the matching OCTOCON_* env var
        // via Parameters:* (see PublishInProcessAsync) and the WithEnvironment calls
        // in InterfoldAppHost.ConfigureApiSelfHostEnv — a typo in either layer would
        // show up here as a missing key or a value mismatch.
        var (config, secrets) = MakeInputs();
        config.Cluster.NodeGroup = NodeGroup.Primary;
        config.Storage.AvatarStorageRoot = "/srv/avatars";
        config.Storage.AvatarPublicBase = "https://cdn.example.com/a/";
        config.Observability.OtlpEndpoint = "http://otel-collector:4317";
        config.Socket.BatchBytesThreshold = 131_072;
        config.Persistence.DbRetryAttempts = 5;
        config.Persistence.DbRetryInitialDelayMs = 250;
        config.Persistence.DbRetryMaxDelayMs = 3_000;
        config.Persistence.HydrationMaxConcurrency = 16;

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, "/base", "/out");

        await Assert.That(replacements.Parameters["NODE_GROUP"]).IsEqualTo("primary");
        await Assert.That(replacements.Parameters["AVATAR_STORAGE_ROOT"]).IsEqualTo("/srv/avatars");
        await Assert.That(replacements.Parameters["AVATAR_PUBLIC_BASE"])
            .IsEqualTo("https://cdn.example.com/a/");
        await Assert.That(replacements.Parameters["OTLP_ENDPOINT"])
            .IsEqualTo("http://otel-collector:4317");
        await Assert.That(replacements.Parameters["SOCKET_BATCH_BYTES_THRESHOLD"]).IsEqualTo("131072");
        await Assert.That(replacements.Parameters["DB_RETRY_ATTEMPTS"]).IsEqualTo("5");
        await Assert.That(replacements.Parameters["DB_RETRY_INITIAL_DELAY_MS"]).IsEqualTo("250");
        await Assert.That(replacements.Parameters["DB_RETRY_MAX_DELAY_MS"]).IsEqualTo("3000");
        await Assert.That(replacements.Parameters["HYDRATION_MAX_CONCURRENCY"]).IsEqualTo("16");
    }

    [Test]
    public async Task BuildEnvReplacementsKeepsAvatarStorageRootBlankSoAppHostCanSubstituteDefault()
    {
        // Phase-1 of the "avatars in a bootstrapped instance" change moved the
        // default avatar-storage-path substitution from the bootstrapper into the
        // AppHost (InterfoldAppHost.cs: DefaultContainerAvatarStorageRoot constant +
        // effectiveAvatarStorageRoot resolution + matching WithVolume mount).
        //
        // The contract this test pins: when the operator leaves
        // BootstrapConfig.Storage.AvatarStorageRoot blank, the rendered .env line
        // for AVATAR_STORAGE_ROOT MUST stay blank (i.e. `AVATAR_STORAGE_ROOT=`), NOT
        // get pre-filled with `/app/data/avatars`. Two reasons:
        //
        //   1. The AppHost reads the .env value AND falls back to its constant when
        //      the value is blank — pre-filling would short-circuit that fallback
        //      and remove the AppHost's only signal that the operator wants the
        //      managed-volume codepath (the same signal the AppHost uses to gate
        //      `api.WithVolume("interfold_avatars", ...)`).
        //   2. Pinning the default in two places (bootstrapper + AppHost) means a
        //      future path change has to land in both — drift is silent. Keeping
        //      the default exclusively in the AppHost makes it the single source
        //      of truth.
        //
        // A sibling check on the matching live behaviour (AppHost wires both the
        // env var AND the volume to the same path) lives in the AppHost integration
        // tests, not here — the bootstrapper unit tests stay scoped to .env content.
        var (config, secrets) = MakeInputs();
        // Default for Storage.AvatarStorageRoot is empty string; leave it untouched
        // so this test exercises the "operator did not configure it" path exactly.
        await Assert.That(config.Storage.AvatarStorageRoot).IsEqualTo(string.Empty)
            .Because("Pre-condition: this test only makes sense when the config-side default is blank.");

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, "/base", "/out");

        await Assert.That(replacements.Parameters.ContainsKey("AVATAR_STORAGE_ROOT")).IsTrue()
            .Because("The key must still be present (Aspire .env post-processing rewrites blank values; missing keys trigger an operator warning).");
        await Assert.That(replacements.Parameters["AVATAR_STORAGE_ROOT"]).IsEqualTo(string.Empty)
            .Because("Blank config MUST round-trip as a blank .env value; the AppHost is responsible for substituting /app/data/avatars at compose-graph build time.");
    }

    [Test]
    public async Task BuildEnvReplacementsEmitsEmptyTuningSlotsForNullables()
    {
        // The four "disabled when empty" tuning fields (Avatar*, OtlpEndpoint, socket
        // threshold) must still emit explicit empty-string entries when unset — rather
        // than being omitted — so the Aspire-emitted blank `.env` lines get rewritten
        // as `KEY=` instead of triggering the "blank value left unfilled" warning in
        // ApplyReplacementsToEnvFile. The API binders normalise the resulting empty
        // env vars to null on read (ApplyStorage / ApplyObservability via NullIfEmpty,
        // socket via TryParseInt) so the not-configured branches still fire.
        var (config, secrets) = MakeInputs();
        // (defaults: Avatar* empty, OtlpEndpoint empty, BatchBytesThreshold null)

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, "/base", "/out");

        await Assert.That(replacements.Parameters.ContainsKey("AVATAR_STORAGE_ROOT")).IsTrue();
        await Assert.That(replacements.Parameters.ContainsKey("AVATAR_PUBLIC_BASE")).IsTrue();
        await Assert.That(replacements.Parameters.ContainsKey("OTLP_ENDPOINT")).IsTrue();
        await Assert.That(replacements.Parameters.ContainsKey("SOCKET_BATCH_BYTES_THRESHOLD")).IsTrue();
        await Assert.That(replacements.Parameters["AVATAR_STORAGE_ROOT"]).IsEqualTo(string.Empty);
        await Assert.That(replacements.Parameters["AVATAR_PUBLIC_BASE"]).IsEqualTo(string.Empty);
        await Assert.That(replacements.Parameters["OTLP_ENDPOINT"]).IsEqualTo(string.Empty);
        await Assert.That(replacements.Parameters["SOCKET_BATCH_BYTES_THRESHOLD"]).IsEqualTo(string.Empty);

        // The five non-nullable tuning fields fall back to their BootstrapConfig
        // property-initialiser defaults so the API container sees the same values it
        // would have used pre-bootstrapper from its compile-time fallbacks.
        await Assert.That(replacements.Parameters["NODE_GROUP"]).IsEqualTo("auxiliary");
        await Assert.That(replacements.Parameters["DB_RETRY_ATTEMPTS"]).IsEqualTo("3");
        await Assert.That(replacements.Parameters["DB_RETRY_INITIAL_DELAY_MS"]).IsEqualTo("100");
        await Assert.That(replacements.Parameters["DB_RETRY_MAX_DELAY_MS"]).IsEqualTo("1500");
        await Assert.That(replacements.Parameters["HYDRATION_MAX_CONCURRENCY"]).IsEqualTo("8");
    }

    [Test]
    public async Task BindMountPathsResolveToAbsoluteUnderOutputDir()
    {
        var (config, secrets) = MakeInputs();
        var baseDir = Path.Combine(Path.GetTempPath(), "interfold-basedir");
        var outputDir = Path.Combine(Path.GetTempPath(), "interfold-outdir");

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, baseDir, outputDir);

        // The /keys bind mount was removed — JWT signing material now lives in internal.secrets.
        await Assert.That(replacements.BindMounts.ContainsKey("interfold-api:/keys")).IsFalse();

        var apiCerts = replacements.BindMounts["interfold-api:/certs"];
        await Assert.That(Path.IsPathFullyQualified(apiCerts)).IsTrue();
        await Assert.That(apiCerts).StartsWith(outputDir);

        // The single-mode Scylla rackdc mount lives under the bootstrapper's baseDir (where the
        // release tarball drops the bundled rackdc.* files), not under outputDir.
        var scyllaRackdc = replacements.BindMounts["scylla:/etc/scylla/cassandra-rackdc.properties"];
        await Assert.That(Path.IsPathFullyQualified(scyllaRackdc)).IsTrue();
        await Assert.That(scyllaRackdc).StartsWith(baseDir);
        await Assert.That(scyllaRackdc).EndsWith("cassandra-rackdc.nam.properties");
    }

    [Test]
    public async Task MultiModeAddsOneBindMountPerScyllaRegion()
    {
        var (config, secrets) = MakeInputs(databaseMode: DatabaseMode.Multi);
        var replacements = PublishPhase.BuildEnvReplacements(config, secrets,
            baseDir: "/base", outputDir: "/out");

        // Multi mode emits 7 region nodes (nam, eur, sam, sas, eas, ocn, gdpr).
        string[] regions = ["nam", "eur", "sam", "sas", "eas", "ocn", "gdpr"];
        foreach (var region in regions)
        {
            var key = $"scylla-{region}:/etc/scylla/cassandra-rackdc.properties";
            await Assert.That(replacements.BindMounts.ContainsKey(key)).IsTrue()
                .Because($"missing bind mount for region {region}");
            await Assert.That(replacements.BindMounts[key]).EndsWith($"cassandra-rackdc.{region}.properties");
        }
    }

    [Test]
    public async Task TranslateDatabaseModeSingleProducesScyllaSingleTopology()
    {
        // single mode is the default and what most installs run. PublishPhase wires the trio
        // straight into the AppHost config, so this assertion pins the mapping byte-for-byte.
        var (includeScylla, includeCassandra, topology) = PublishPhase.TranslateDatabaseMode(DatabaseMode.Single);

        await Assert.That(includeScylla).IsTrue();
        await Assert.That(includeCassandra).IsFalse();
        await Assert.That(topology).IsEqualTo(ScyllaTopology.Single);
        await Assert.That(topology.ToWireValue()).IsEqualTo("single");
    }

    [Test]
    public async Task TranslateDatabaseModeMultiProducesScyllaMultiTopology()
    {
        // multi mode keeps Cassandra off and flips topology to multi - this is the only
        // route to the 7-region Scylla layout from the bootstrapper.
        var (includeScylla, includeCassandra, topology) = PublishPhase.TranslateDatabaseMode(DatabaseMode.Multi);

        await Assert.That(includeScylla).IsTrue();
        await Assert.That(includeCassandra).IsFalse();
        await Assert.That(topology).IsEqualTo(ScyllaTopology.Multi);
        await Assert.That(topology.ToWireValue()).IsEqualTo("multi");
    }

    [Test]
    public async Task TranslateDatabaseModeCassandraSwapsBackends()
    {
        // cassandra mode is the only configuration that disables Scylla entirely.
        // Topology stays "single" because the cassandra branch in InterfoldAppHost
        // ignores topology, but emitting "single" keeps the parameter set well-formed.
        var (includeScylla, includeCassandra, topology) = PublishPhase.TranslateDatabaseMode(DatabaseMode.Cassandra);

        await Assert.That(includeScylla).IsFalse();
        await Assert.That(includeCassandra).IsTrue();
        await Assert.That(topology).IsEqualTo(ScyllaTopology.Single);
        await Assert.That(topology.ToWireValue()).IsEqualTo("single");
    }

    [Test]
    public async Task CassandraModeFillsCassandraImageEnvKey()
    {
        var (config, secrets) = MakeInputs(databaseMode: DatabaseMode.Cassandra);
        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, "/base", "/out");

        await Assert.That(replacements.Parameters.ContainsKey("CASSANDRA_IMAGE")).IsTrue()
            .Because("Aspire emits image: \"${CASSANDRA_IMAGE}\" for the Dockerfile service");
        await Assert.That(replacements.Parameters["CASSANDRA_IMAGE"])
            .IsEqualTo(CassandraImagePhase.LocalImageTag);
    }

    [Test]
    public async Task NonCassandraModesOmitCassandraImageEnvKey()
    {
        var (config, secrets) = MakeInputs(databaseMode: DatabaseMode.Single);
        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, "/base", "/out");

        await Assert.That(replacements.Parameters.ContainsKey("CASSANDRA_IMAGE")).IsFalse()
            .Because("Scylla-only stacks must not carry an unused CASSANDRA_IMAGE entry");
    }

    [Test]
    public async Task TranslateDatabaseModeRejectsUnknownValue()
    {
        // ConfigPhase.Validate is the operator-facing rejection point, but the helper
        // also throws so internal callers that bypass validation surface a clear error
        // instead of silently emitting an empty parameter set. Casting outside the enum
        // range simulates the "someone bypassed the type system" scenario.
        var ex = Assert.Throws<InvalidOperationException>(() => PublishPhase.TranslateDatabaseMode((DatabaseMode)999));

        await Assert.That(ex.Message).Contains("databaseMode");
    }

    [Test]
    public async Task WebHttpsOffDoesNotAddOctoconWebBindMounts()
    {
        var (config, secrets) = MakeInputs();
        config.Deployment.IncludeWeb = false;
        config.Deployment.WebHttps = false;
        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, "/base", "/out");

        await Assert.That(replacements.BindMounts.ContainsKey("octocon-web:/certs")).IsFalse()
            .Because("octocon-web:/certs must only appear when deployment.webHttps=true");
        await Assert.That(replacements.BindMounts.ContainsKey(
            "octocon-web:/etc/nginx/templates/default.conf.template")).IsFalse()
            .Because("nginx template bind mount must only appear when deployment.webHttps=true");
    }

    [Test]
    public async Task IncludeWebOnlyDoesNotAddOctoconWebBindMounts()
    {
        // includeWeb=true, webHttps=false is the HTTP-only debug variant: the wasm container
        // ships in the compose stack but nginx never reads a leaf cert, so neither the /certs
        // bind mount nor the nginx envsubst template bind mount belong in the .env. (The
        // include-vs-TLS-mount asymmetry is the whole point of decoupling these two toggles.)
        var (config, secrets) = MakeInputs();
        config.Deployment.IncludeWeb = true;
        config.Deployment.WebHttps = false;
        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, "/base", "/out");

        await Assert.That(replacements.BindMounts.ContainsKey("octocon-web:/certs")).IsFalse()
            .Because("HTTP-only web container does not read leaf certs — /certs bind mount must stay off");
        await Assert.That(replacements.BindMounts.ContainsKey(
            "octocon-web:/etc/nginx/templates/default.conf.template")).IsFalse()
            .Because("HTTP-only web container does not render the TLS template — nginx mount must stay off");
    }

    [Test]
    public async Task IncludeWebAndWebHttpsBothOnAddsCertsAndNginxTemplateBindMounts()
    {
        // The explicit "both flags on" combination — operator opts in to both the container and
        // TLS termination at it. Same wiring as the webHttps-only-on path (the includeWeb=true
        // doesn't change the bind-mount set), just locking the contract down so a future
        // refactor that gates the mounts on the OR of the two flags doesn't accidentally drop
        // them in either input shape.
        var (config, secrets) = MakeInputs();
        config.Deployment.IncludeWeb = true;
        config.Deployment.WebHttps = true;
        var baseDir = Path.Combine(Path.GetTempPath(), "interfold-basedir");
        var outputDir = Path.Combine(Path.GetTempPath(), "interfold-outdir");

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, baseDir, outputDir);

        await Assert.That(replacements.BindMounts.ContainsKey("octocon-web:/certs")).IsTrue();
        await Assert.That(replacements.BindMounts.ContainsKey(
            "octocon-web:/etc/nginx/templates/default.conf.template")).IsTrue();
    }

    [Test]
    public async Task WebHttpsOnAddsCertsAndNginxTemplateBindMounts()
    {
        var (config, secrets) = MakeInputs();
        config.Deployment.WebHttps = true;
        var baseDir = Path.Combine(Path.GetTempPath(), "interfold-basedir");
        var outputDir = Path.Combine(Path.GetTempPath(), "interfold-outdir");

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, baseDir, outputDir);

        // /certs is shared with the API: both services bind-mount {outputDir}/certs/, so the leaf
        // PFX (read by Kestrel) and the leaf CRT/KEY (read by nginx) come from the same on-disk
        // material rotated by CertificatePhase.
        await Assert.That(replacements.BindMounts.ContainsKey("octocon-web:/certs")).IsTrue();
        var webCerts = replacements.BindMounts["octocon-web:/certs"];
        await Assert.That(Path.IsPathFullyQualified(webCerts)).IsTrue();
        await Assert.That(webCerts).StartsWith(outputDir);
        await Assert.That(replacements.BindMounts["interfold-api:/certs"]).IsEqualTo(webCerts)
            .Because("API and web tiers must read from the same on-disk certs directory");

        // The nginx template lives next to the bootstrapper binary (baseDir), not under outputDir,
        // mirroring how cassandra-rackdc.* properties are shipped.
        const string nginxKey = "octocon-web:/etc/nginx/templates/default.conf.template";
        await Assert.That(replacements.BindMounts.ContainsKey(nginxKey)).IsTrue();
        var nginxTemplate = replacements.BindMounts[nginxKey];
        await Assert.That(Path.IsPathFullyQualified(nginxTemplate)).IsTrue();
        await Assert.That(nginxTemplate).StartsWith(baseDir);
        await Assert.That(nginxTemplate).EndsWith("default.conf.template");
    }

    [Test]
    public async Task ApiImageOverrideDoesNotLeakIntoEnvReplacements()
    {
        // The API container image flows through Aspire's `Parameters:api-image` configuration
        // (which Aspire bakes into the compose YAML as a `image:` line), NOT through the .env.
        // This test pins that contract: changing config.ApiImage must not change the env
        // replacement keys or values — only the compose YAML.
        var (configA, secretsA) = MakeInputs(apiImage: "ghcr.io/azyyyyyy/interfold-api:v1.2.3");
        var (configB, secretsB) = MakeInputs(apiImage: "private-registry.example.com/api:custom-tag");
        // Pin secrets so the only difference is ApiImage.
        secretsB.PostgresPassword = secretsA.PostgresPassword;
        secretsB.PostgresInitPassword = secretsA.PostgresInitPassword;
        secretsB.PostgresAdminPassword = secretsA.PostgresAdminPassword;
        secretsB.ScyllaPassword = secretsA.ScyllaPassword;
        secretsB.ScyllaAdminPassword = secretsA.ScyllaAdminPassword;
        secretsB.EncryptionPrivateKeyB64 = secretsA.EncryptionPrivateKeyB64;

        var a = PublishPhase.BuildEnvReplacements(configA, secretsA, "/base", "/out");
        var b = PublishPhase.BuildEnvReplacements(configB, secretsB, "/base", "/out");

        // Every key on both sides should map to the same value.
        await Assert.That(a.Parameters.Count).IsEqualTo(b.Parameters.Count);
        foreach (var kv in a.Parameters)
        {
            await Assert.That(b.Parameters.ContainsKey(kv.Key)).IsTrue();
            await Assert.That(b.Parameters[kv.Key]).IsEqualTo(kv.Value);
        }

        // And no key named anything image-y appears in either set.
        foreach (var key in a.Parameters.Keys)
        {
            await Assert.That(key.Contains("IMAGE", StringComparison.OrdinalIgnoreCase)).IsFalse()
                .Because($"unexpected image-related key '{key}' leaked into env replacements");
        }
    }

    [Test]
    public async Task WebServerNameSkipsCidrEntries()
    {
        // nginx accepts DNS names and bare IPs as server_name but does NOT accept CIDR
        // notation. PickServerName must walk past any CIDR entries to the first leaf-eligible
        // host. Order matters here — the CIDR comes first in the list so the test fails loudly
        // if PickServerName falls back to `_` or yields the CIDR string instead of skipping.
        var serverName = PublishPhase.PickServerName(["192.168.1.0/24", "api.example.com"]);
        await Assert.That(serverName).IsEqualTo("api.example.com");
    }

    [Test]
    public async Task WebServerNameFallsBackToCatchAllWhenAllCidr()
    {
        // Defence-in-depth: ConfigPhase.Validate already rejects an all-CIDR list, but the
        // direct InterfoldAppHost.Configure path (dev callers that bypass the bootstrapper)
        // doesn't run Validate. PickServerName must still produce a working server_name in
        // that branch — `_` is the nginx catch-all that accepts any Host header.
        var serverName = PublishPhase.PickServerName(["10.0.0.0/8", "fe80::/64"]);
        await Assert.That(serverName).IsEqualTo("_");
    }

    [Test]
    public async Task WebServerNamePicksIpLiteralAsServerName()
    {
        // LAN-only deployments (no DNS) must end up with the bare IP as server_name. nginx
        // happily accepts dotted-quad and bracketed-or-bare IPv6 there.
        var serverName = PublishPhase.PickServerName(["192.168.1.42"]);
        await Assert.That(serverName).IsEqualTo("192.168.1.42");
    }

    [Test]
    public async Task StampCassandraPullPolicyNeverInsertsPolicyAfterImageLine()
    {
        // The stamper's job: after the `image: "${CASSANDRA_IMAGE}"` line, insert
        // `pull_policy: never` at the same indent so `docker compose pull` skips the
        // service instead of failing on the non-registry-backed tag. Docker Compose's
        // pull command explicitly skips services with pull_policy: never — the
        // integration test in UpdateImagesCassandraModeTests confirms the observable
        // "Skipped" behaviour end-to-end; this unit test locks down the file mutation
        // shape.
        var tmp = Path.Combine(Path.GetTempPath(), $"compose-stamp-{Guid.NewGuid():N}.yaml");
        try
        {
            var original = string.Join("\n", new[]
            {
                "services:",
                "  cassandra:",
                "    image: \"${CASSANDRA_IMAGE}\"",
                "    volumes:",
                "      - cassandra-data:/var/lib/cassandra",
                "  interfold-api:",
                "    image: interfold-api:test",
                "",
            });
            await File.WriteAllTextAsync(tmp, original);

            PublishPhase.StampCassandraPullPolicyNever(tmp);

            var lines = await File.ReadAllLinesAsync(tmp);
            var imageIdx = Array.FindIndex(lines, l => l.Contains("${CASSANDRA_IMAGE}", StringComparison.Ordinal));
            await Assert.That(imageIdx).IsGreaterThanOrEqualTo(0)
                .Because("baseline: the anchor line must still be present after stamping");
            await Assert.That(lines[imageIdx + 1]).IsEqualTo("    pull_policy: never")
                .Because("stamp must land on the very next line, at the same 4-space indent as the image key");
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Test]
    public async Task StampCassandraPullPolicyNeverIsIdempotent()
    {
        // Re-running `bootstrap publish` (or `bootstrap` itself) is a supported operator
        // action — the stamper must not double-stamp. Idempotency here means: calling the
        // stamper twice in a row lands exactly one `pull_policy: never` line, not two.
        var tmp = Path.Combine(Path.GetTempPath(), $"compose-stamp-idem-{Guid.NewGuid():N}.yaml");
        try
        {
            var original = string.Join("\n", new[]
            {
                "services:",
                "  cassandra:",
                "    image: \"${CASSANDRA_IMAGE}\"",
                "    volumes:",
                "      - cassandra-data:/var/lib/cassandra",
                "",
            });
            await File.WriteAllTextAsync(tmp, original);

            PublishPhase.StampCassandraPullPolicyNever(tmp);
            PublishPhase.StampCassandraPullPolicyNever(tmp);

            var lines = await File.ReadAllLinesAsync(tmp);
            var count = lines.Count(l => string.Equals(l.Trim(), "pull_policy: never", StringComparison.Ordinal));
            await Assert.That(count).IsEqualTo(1)
                .Because("second invocation must be a no-op — one policy line, not two");
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Test]
    public async Task StampCassandraPullPolicyNeverIsNoOpWhenCassandraAnchorAbsent()
    {
        // Defensive branch: a scylla-mode compose file (no ${CASSANDRA_IMAGE} anchor)
        // handed to the stamper by accident must leave the file untouched rather than
        // throwing or corrupting a random service block. The publish path only calls
        // the stamper when CassandraImagePhase.IsCassandraDeployment(config) is true,
        // so this is belt-and-braces — but it's cheap coverage and keeps the stamper
        // safe to call in any future context (e.g. an operator running it manually).
        var tmp = Path.Combine(Path.GetTempPath(), $"compose-stamp-noop-{Guid.NewGuid():N}.yaml");
        try
        {
            var original = string.Join("\n", new[]
            {
                "services:",
                "  scylla:",
                "    image: scylladb/scylla:2026.1",
                "  interfold-api:",
                "    image: interfold-api:test",
                "",
            });
            await File.WriteAllTextAsync(tmp, original);

            PublishPhase.StampCassandraPullPolicyNever(tmp);

            var after = await File.ReadAllTextAsync(tmp);
            await Assert.That(after.Contains("pull_policy", StringComparison.Ordinal)).IsFalse()
                .Because("no ${CASSANDRA_IMAGE} anchor means nothing to stamp; the file must be untouched");
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
