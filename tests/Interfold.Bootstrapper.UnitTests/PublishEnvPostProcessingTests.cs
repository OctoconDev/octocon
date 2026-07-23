using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using Interfold.Contracts.Enums;
using TUnit.Core;
using Interfold.Contracts.Configuration;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>Sub-second drift check on <see cref="PublishPhase.BuildEnvReplacements"/>. Integration
/// tests cover the emitted file end-to-end; these lock the pure key set.</summary>
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
        // Deployment.Hosts has no placeholder; without a seed ResolveDerivedDefaults has
        // nothing to feed CallbackBaseUrl / JwtAuthority / CorsAllowedOrigins from.
        config.Deployment.Hosts = ["api.example.com"];
        // OAuth client secrets flow into internal.secrets, not env; setting exercises the
        // irrelevant path.
        config.OAuth.GoogleClientSecret = "google-secret-from-config";
        config.OAuth.DiscordClientSecret = "discord-secret-from-config";

        // Mirror ConfigPhase.Validate's post-derivation snapshot without invoking the full
        // Validate (publish tests deliberately allow shapes Validate would reject).
        ConfigPhase.ResolveDerivedDefaults(config);
        return (config, SecretsPhase.Generate());
    }

    [Test]
    public async Task BuildEnvReplacementsProducesAllRequiredParameterKeys()
    {
        var (config, secrets) = MakeInputs();
        // Pin all three OAuth IDs so IsNotEmpty is meaningful; empty-still-emits behaviour
        // has its own test below.
        config.OAuth.GoogleClientId = "google-client-id";
        config.OAuth.DiscordClientId = "discord-client-id";
        config.OAuth.AppleClientId = "apple-client-id";
        // Pin the four nullable tuning fields for the same reason.
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
            "GOOGLE_OAUTH_CLIENT_ID",
            "DISCORD_OAUTH_CLIENT_ID",
            "APPLE_OAUTH_CLIENT_ID",
            "SCYLLA_KEYSPACE",
            "OAUTH_CALLBACK_BASE_URL",
            "JWT_AUTHORITY",
            "JWT_AUDIENCE",
            "CORS_ALLOWED_ORIGINS",
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
        // Contract: OAuth client IDs copy verbatim into the env dict; a rename either side
        // shows up as a missing key or value mismatch here.
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
        // Empty client IDs mean "provider disabled"; keys MUST still emit as `KEY=` so
        // ApplyReplacementsToEnvFile doesn't warn on unfilled blanks.
        var (config, secrets) = MakeInputs();

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
        // POSTGRES_DB must round-trip verbatim so the API connection string lines up with
        // the database DatabaseInitPhase actually creates.
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
        // Single mode: 24 param keys + 2 bind mounts (/certs, scylla rackdc) = 26.
        await Assert.That(total).IsEqualTo(26);
    }

    [Test]
    public async Task BuildEnvReplacementsCarriesApiRuntimeFromConfig()
    {
        // ScyllaKeyspace + every ApiRuntime field must round-trip verbatim; a typo either
        // side surfaces here as a missing key or value mismatch.
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
        // Comma-separated on OCTOCON_CORS_ALLOWED_ORIGINS; API CORS startup splits on ','.
        await Assert.That(replacements.Parameters["CORS_ALLOWED_ORIGINS"])
            .IsEqualTo("https://app.example.com,https://admin.example.com");
    }

    [Test]
    public async Task BuildEnvReplacementsDerivesApiRuntimeFromDeploymentWhenUnset()
    {
        // Blank apiRuntime.* is derived from deployment.hosts + deployment.webHttps via
        // ResolveDerivedDefaults; the derivation must round-trip through publish unchanged.
        var config = new BootstrapConfig
        {
            Deployment =
            {
                Hosts = ["api.example.com", "admin.example.com"],
                WebHttps = true,
            },
        };
        // Run derivation explicitly (MakeInputs would inject non-blank defaults) to exercise
        // the empty-apiRuntime path.
        ConfigPhase.ResolveDerivedDefaults(config);
        var secrets = SecretsPhase.Generate();

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, "/base", "/out");

        // API URL is always https + Ports.ApiHttps (5001) — Kestrel binds the leaf PFX
        // regardless of Deployment.WebHttps, which only governs the web container.
        await Assert.That(replacements.Parameters["OAUTH_CALLBACK_BASE_URL"])
            .IsEqualTo("https://api.example.com:5001");
        await Assert.That(replacements.Parameters["JWT_AUTHORITY"])
            .IsEqualTo("https://api.example.com:5001");
        // JwtAudience is a property-initialiser default, not derived.
        await Assert.That(replacements.Parameters["JWT_AUDIENCE"]).IsEqualTo("octocon");
        // CORS: scheme=WebHttps, port=Ports.WebHttps (8081), one entry per non-CIDR host.
        await Assert.That(replacements.Parameters["CORS_ALLOWED_ORIGINS"])
            .IsEqualTo("https://api.example.com:8081,https://admin.example.com:8081");
        await Assert.That(replacements.Parameters["SCYLLA_KEYSPACE"]).IsEqualTo("nam");
    }

    [Test]
    public async Task BuildEnvReplacementsCarriesTuningFromConfig()
    {
        // Every operator tuning field must round-trip verbatim.
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
        // Blank AVATAR_STORAGE_ROOT must round-trip blank. The AppHost owns the default
        // (DefaultContainerAvatarStorageRoot) and reads blank as "use my constant + managed
        // volume"; pre-filling here would kill that signal and duplicate the default.
        var (config, secrets) = MakeInputs();
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
        // "Disabled when empty" tuning fields must emit `KEY=` (not be omitted); API binders
        // normalise the empty vars back to null so the not-configured branches still fire.
        var (config, secrets) = MakeInputs();

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, "/base", "/out");

        await Assert.That(replacements.Parameters.ContainsKey("AVATAR_STORAGE_ROOT")).IsTrue();
        await Assert.That(replacements.Parameters.ContainsKey("AVATAR_PUBLIC_BASE")).IsTrue();
        await Assert.That(replacements.Parameters.ContainsKey("OTLP_ENDPOINT")).IsTrue();
        await Assert.That(replacements.Parameters.ContainsKey("SOCKET_BATCH_BYTES_THRESHOLD")).IsTrue();
        await Assert.That(replacements.Parameters["AVATAR_STORAGE_ROOT"]).IsEqualTo(string.Empty);
        await Assert.That(replacements.Parameters["AVATAR_PUBLIC_BASE"]).IsEqualTo(string.Empty);
        await Assert.That(replacements.Parameters["OTLP_ENDPOINT"]).IsEqualTo(string.Empty);
        await Assert.That(replacements.Parameters["SOCKET_BATCH_BYTES_THRESHOLD"]).IsEqualTo(string.Empty);

        // Non-nullable tuning fields fall back to their property-initialiser defaults.
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

        // /keys was removed — JWT signing material lives in internal.secrets now.
        await Assert.That(replacements.BindMounts.ContainsKey("interfold-api:/keys")).IsFalse();

        var apiCerts = replacements.BindMounts["interfold-api:/certs"];
        await Assert.That(Path.IsPathFullyQualified(apiCerts)).IsTrue();
        await Assert.That(apiCerts).StartsWith(outputDir);

        // Scylla rackdc lives under baseDir (tarball drop location), not outputDir.
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
        var (includeScylla, includeCassandra, topology) = PublishPhase.TranslateDatabaseMode(DatabaseMode.Single);

        await Assert.That(includeScylla).IsTrue();
        await Assert.That(includeCassandra).IsFalse();
        await Assert.That(topology).IsEqualTo(ScyllaTopology.Single);
        await Assert.That(topology.ToWireValue()).IsEqualTo("single");
    }

    [Test]
    public async Task TranslateDatabaseModeMultiProducesScyllaMultiTopology()
    {
        var (includeScylla, includeCassandra, topology) = PublishPhase.TranslateDatabaseMode(DatabaseMode.Multi);

        await Assert.That(includeScylla).IsTrue();
        await Assert.That(includeCassandra).IsFalse();
        await Assert.That(topology).IsEqualTo(ScyllaTopology.Multi);
        await Assert.That(topology.ToWireValue()).IsEqualTo("multi");
    }

    [Test]
    public async Task TranslateDatabaseModeCassandraSwapsBackends()
    {
        // Cassandra mode disables Scylla entirely; topology "single" is filler (the
        // cassandra branch in InterfoldAppHost ignores topology).
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
        // Fail-fast for callers that bypass ConfigPhase.Validate.
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
        // HTTP-only debug variant: nginx never reads a leaf cert, so neither cert nor
        // template mounts belong in .env. Include-vs-TLS-mount decoupling is intentional.
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
        // Locks the "both flags on" shape so a future OR-gated refactor can't silently drop
        // the mounts in either input form.
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

        // API + web share {outputDir}/certs so Kestrel's PFX and nginx's CRT/KEY come from
        // the same CertificatePhase output.
        await Assert.That(replacements.BindMounts.ContainsKey("octocon-web:/certs")).IsTrue();
        var webCerts = replacements.BindMounts["octocon-web:/certs"];
        await Assert.That(Path.IsPathFullyQualified(webCerts)).IsTrue();
        await Assert.That(webCerts).StartsWith(outputDir);
        await Assert.That(replacements.BindMounts["interfold-api:/certs"]).IsEqualTo(webCerts)
            .Because("API and web tiers must read from the same on-disk certs directory");

        // nginx template lives next to the binary (baseDir), mirroring rackdc props.
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
        // ApiImage flows via Aspire Parameters:api-image into compose YAML, not .env.
        var (configA, secretsA) = MakeInputs(apiImage: "ghcr.io/azyyyyyy/interfold-api:v1.2.3");
        var (configB, secretsB) = MakeInputs(apiImage: "private-registry.example.com/api:custom-tag");
        secretsB.PostgresPassword = secretsA.PostgresPassword;
        secretsB.PostgresInitPassword = secretsA.PostgresInitPassword;
        secretsB.PostgresAdminPassword = secretsA.PostgresAdminPassword;
        secretsB.ScyllaPassword = secretsA.ScyllaPassword;
        secretsB.ScyllaAdminPassword = secretsA.ScyllaAdminPassword;
        secretsB.EncryptionPrivateKeyB64 = secretsA.EncryptionPrivateKeyB64;

        var a = PublishPhase.BuildEnvReplacements(configA, secretsA, "/base", "/out");
        var b = PublishPhase.BuildEnvReplacements(configB, secretsB, "/base", "/out");

        await Assert.That(a.Parameters.Count).IsEqualTo(b.Parameters.Count);
        foreach (var kv in a.Parameters)
        {
            await Assert.That(b.Parameters.ContainsKey(kv.Key)).IsTrue();
            await Assert.That(b.Parameters[kv.Key]).IsEqualTo(kv.Value);
        }

        foreach (var key in a.Parameters.Keys)
        {
            await Assert.That(key.Contains("IMAGE", StringComparison.OrdinalIgnoreCase)).IsFalse()
                .Because($"unexpected image-related key '{key}' leaked into env replacements");
        }
    }

    [Test]
    public async Task WebServerNameSkipsCidrEntries()
    {
        // nginx server_name accepts DNS and IPs but not CIDRs; PickServerName must skip.
        var serverName = PublishPhase.PickServerName(["192.168.1.0/24", "api.example.com"]);
        await Assert.That(serverName).IsEqualTo("api.example.com");
    }

    [Test]
    public async Task WebServerNameFallsBackToCatchAllWhenAllCidr()
    {
        // Direct InterfoldAppHost.Configure callers skip Validate; `_` is nginx's catch-all.
        var serverName = PublishPhase.PickServerName(["10.0.0.0/8", "fe80::/64"]);
        await Assert.That(serverName).IsEqualTo("_");
    }

    [Test]
    public async Task WebServerNamePicksIpLiteralAsServerName()
    {
        // LAN-only deployments (no DNS) get the bare IP; nginx accepts dotted-quad + v6.
        var serverName = PublishPhase.PickServerName(["192.168.1.42"]);
        await Assert.That(serverName).IsEqualTo("192.168.1.42");
    }

    [Test]
    public async Task StampCassandraPullPolicyNeverInsertsPolicyAfterImageLine()
    {
        // Inserts `pull_policy: never` at the image indent so `docker compose pull` skips
        // the non-registry-backed tag. End-to-end behaviour tested in UpdateImagesCassandraModeTests.
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
        // Reruns of `bootstrap publish` must not double-stamp.
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
        // No ${CASSANDRA_IMAGE} anchor → leave the file alone; keeps the stamper safe
        // to call from any future context.
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

    [Test]
    public async Task EnumerateSharedAspireParametersConfigKeyMatchesEnvKeyKebabToUpperSnake()
    {
        // EnvKey must equal upper-snake(kebab parameter name); a hand-typed EnvKey would
        // otherwise silently blank the corresponding OCTOCON_* env var.
        var (config, secrets) = MakeInputs();

        var seenConfigKeys = new HashSet<string>(StringComparer.Ordinal);
        var seenEnvKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (configKey, envKey, _) in PublishPhase.EnumerateSharedAspireParameters(config, secrets))
        {
            await Assert.That(seenConfigKeys.Add(configKey)).IsTrue()
                .Because($"config-key '{configKey}' appears twice in EnumerateSharedAspireParameters");
            await Assert.That(seenEnvKeys.Add(envKey)).IsTrue()
                .Because($"env-key '{envKey}' appears twice in EnumerateSharedAspireParameters");

            // ToParameterName also enforces the "Parameters:*" namespace by throwing for
            // graph-only Ports:* entries.
            var bareName = AppHostParameterKeys.ToParameterName(configKey);
            var expectedEnvKey = bareName.Replace('-', '_').ToUpperInvariant();
            await Assert.That(envKey).IsEqualTo(expectedEnvKey)
                .Because($"env-key '{envKey}' must be the upper-snake-cased form of the kebab-cased Aspire parameter '{bareName}' (config-key '{configKey}')");
        }

        // Spec-frozen at 24 — bump this AND the enumerator together.
        await Assert.That(seenConfigKeys.Count).IsEqualTo(24)
            .Because("shared-parameter count is spec-frozen at 24; update BOTH the enumerator AND this assertion together");
    }
}
