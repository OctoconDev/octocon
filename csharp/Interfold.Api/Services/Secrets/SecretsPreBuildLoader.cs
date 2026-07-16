using Interfold.Contracts;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Secrets;
using Npgsql;

namespace Interfold.Api.Services.Secrets;

/// <summary>
/// Populates the API's <see cref="SecretsSnapshot"/> before <c>WebApplicationBuilder.Build()</c>,
/// unifying the retired post-Build <c>SecretsSnapshotLoader</c> hosted service and the bare-Npgsql
/// leaf-PFX loader that used to live in <c>Program.cs</c>. Called from <c>Program.cs</c>
/// immediately after <c>AddServiceDefaults()</c>; the returned snapshot is registered as an
/// instance singleton (valid pre-<c>Build()</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Branch.</b> When <c>OCTOCON_POSTGRES_CONNECTION</c> is set, every snapshot row plus the leaf
/// PFX password is fetched in one batched query on a bare <see cref="NpgsqlConnection"/> —
/// <see cref="ISecretsStore"/> isn't built yet. Otherwise the four mandatory auth secrets are read
/// from the same <c>OCTOCON_INMEMORY_SECRETS_SEED:*</c> env-var family that
/// <c>AddInMemoryPersistence</c> uses, just one layer earlier; OAuth / Firebase-client / FCM rows
/// stay null (no InMemory seed path exists, matching the retired <c>InMemorySecretsStore</c>).
/// The bare connection has no retry, matching the retired leaf-PFX loader.
/// </para>
/// <para>
/// <b>Dedicated pool.</b> Npgsql keys pools on the canonical connection string, so
/// <see cref="WithDedicatedPoolIdentity"/> rewrites it with a distinct <c>Application Name</c> and
/// bounded <c>Maximum Pool Size</c>. Without this, parallel <c>InterfoldWebApplicationFactory</c>
/// builds contend for the fixture's pinned 5-slot app pool and blow the 15s pool timeout; an
/// earlier <c>Pooling=false</c> attempt just pushed the problem down to Postgres's
/// <c>max_connections</c>. The dedicated pool fixes both.
/// </para>
/// </remarks>
internal static class SecretsPreBuildLoader
{
    /// <summary>
    /// Every row consulted by the API's <c>IPostConfigureOptions</c> patchers. Keep in sync with
    /// <see cref="AuthenticationSecretsPostConfigure"/>, <see cref="FirebaseClientSecretsPostConfigure"/>,
    /// and <see cref="FcmSecretsPostConfigure"/>.
    /// </summary>
    private static readonly SecretsStoreKey[] SnapshotKeys =
    [
        // Auth secrets consumed by AuthenticationSecretsPostConfigure
        SecretsStoreKeys.OAuthGoogleClientSecret,
        SecretsStoreKeys.OAuthDiscordClientSecret,
        SecretsStoreKeys.OAuthAppleClientSecret,
        SecretsStoreKeys.EncryptionPepper,
        SecretsStoreKeys.AuthDeepLinkSecret,
        SecretsStoreKeys.AuthJwtRsa256PrivatePem,
        SecretsStoreKeys.AuthJwtEs256PrivatePem,

        // Firebase client-init rows consumed by FirebaseClientSecretsPostConfigure
        SecretsStoreKeys.FirebaseClientAndroid,
        SecretsStoreKeys.FirebaseClientIos,
        SecretsStoreKeys.FirebaseClientWeb,

        // FCM v1 service-account credential consumed by FcmSecretsPostConfigure
        SecretsStoreKeys.FcmServiceAccountJson,
    ];

    public static SecretsSnapshot Load(IConfigurationBuilder cfg)
    {
        var config = cfg.Build();
        var snapshot = new SecretsSnapshot();

        if (EnumWireExtensions.ParsePersistenceMode(config[OctoconEnvKeys.Persistence]) == PersistenceMode.ScyllaPostgres)
        {
            var pgConn = config[OctoconEnvKeys.PostgresConnection];
            if (string.IsNullOrWhiteSpace(pgConn))
            {
                throw new InvalidOperationException("Postgres connection string is not configured.");
            }
            
            var rows = FetchFromPostgres(pgConn);
            snapshot.Populate(BuildSnapshotBuffer(rows));
            var leafPfxPassword = rows.GetValueOrDefault(SecretsStoreKeys.CertsLeafPfxPassword.Value);
            ApplyLeafPfxPasswordIfNeeded(cfg, config, pgConnPresent: true, leafPfxPassword);
        }
        else
        {
            snapshot.Populate(BuildInMemorySeedBuffer(config));
            ApplyLeafPfxPasswordIfNeeded(cfg, config, pgConnPresent: false, leafPfxPassword: null);
        }

        return snapshot;
    }

    private static Dictionary<SecretsStoreKey, string?> BuildSnapshotBuffer(Dictionary<string, string?> rows)
    {
        var buffer = new Dictionary<SecretsStoreKey, string?>(SnapshotKeys.Length);
        foreach (var key in SnapshotKeys)
        {
            buffer[key] = rows.GetValueOrDefault(key.Value);
        }
        return buffer;
    }

    /// <summary>
    /// Reads the four mandatory auth secrets from the <c>OCTOCON_INMEMORY_SECRETS_SEED:*</c>
    /// env-var family, mirroring <c>AddInMemoryPersistence</c>'s seeding one layer earlier.
    /// </summary>
    private static Dictionary<SecretsStoreKey, string?> BuildInMemorySeedBuffer(IConfigurationRoot config) => new()
    {
        [SecretsStoreKeys.EncryptionPepper] = config[OctoconEnvKeys.InMemorySecretsSeedEncryptionPepper],
        [SecretsStoreKeys.AuthJwtEs256PrivatePem] = config[OctoconEnvKeys.InMemorySecretsSeedAuthJwtEs256PrivatePem],
        [SecretsStoreKeys.AuthDeepLinkSecret] = config[OctoconEnvKeys.InMemorySecretsSeedAuthDeepLinkSecret],
        [SecretsStoreKeys.AuthJwtRsa256PrivatePem] = config[OctoconEnvKeys.InMemorySecretsSeedAuthJwtRsa256PrivatePem],
    };

    /// <summary>
    /// One batched round trip for every snapshot row plus the leaf PFX password. Any failure
    /// surfaces as a fail-fast <see cref="InvalidOperationException"/>, same effect as the retired
    /// <c>SecretsSnapshotLoader.StartingAsync</c>.
    /// </summary>
    private static Dictionary<string, string?> FetchFromPostgres(string pgConn)
    {
        var rows = new Dictionary<string, string?>(StringComparer.Ordinal);
        var loaderConn = WithDedicatedPoolIdentity(pgConn);
        try
        {
            using var conn = new NpgsqlConnection(loaderConn);
            conn.Open();
            using var cmd = new NpgsqlCommand(
                "SELECT key, value FROM internal.secrets WHERE key = ANY(@keys)", conn);

            var keys = new string[SnapshotKeys.Length + 1];
            for (var i = 0; i < SnapshotKeys.Length; i++)
            {
                keys[i] = SnapshotKeys[i].Value;
            }
            keys[^1] = SecretsStoreKeys.CertsLeafPfxPassword.Value;
            cmd.Parameters.AddWithValue("keys", keys);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to fetch startup secrets from internal.secrets. Ensure Postgres is " +
                "reachable at startup and that DatabaseInitPhase has seeded the required rows.", ex);
        }

        return rows;
    }

    /// <summary>
    /// Loader's distinct <c>application_name</c>, giving it its own Npgsql pool identity and
    /// surfacing loader connections in <c>pg_stat_activity</c> for triage.
    /// </summary>
    internal const string LoaderApplicationName = "octocon-secrets-preload";

    /// <summary>
    /// Bounded pool ceiling for the loader. 10 is ample for observed peak factory-build
    /// concurrency (~3-5) and, with the fixture's 5-slot app pool, keeps our total Postgres
    /// client footprint at 15 — well under the default <c>max_connections=100</c>.
    /// </summary>
    internal const int LoaderMaxPoolSize = 10;

    /// <summary>
    /// Returns <paramref name="pgConn"/> rewritten to route through a dedicated Npgsql pool:
    /// <c>Application Name=</c><see cref="LoaderApplicationName"/> shifts the canonical string
    /// (distinct pool identity), <c>Maximum Pool Size=</c><see cref="LoaderMaxPoolSize"/> caps the
    /// footprint, and <c>Pooling</c> stays on. Round-tripped through
    /// <see cref="NpgsqlConnectionStringBuilder"/> so operator-supplied keywords survive intact;
    /// both values are overwritten rather than defaulted, since any pre-existing values would
    /// defeat the isolation this helper guarantees. Extracted so
    /// <c>SecretsPreBuildLoaderPoolingTests</c> can exercise it without spinning up Postgres.
    /// </summary>
    internal static string WithDedicatedPoolIdentity(string pgConn)
    {
        var builder = new NpgsqlConnectionStringBuilder(pgConn)
        {
            ApplicationName = LoaderApplicationName,
            MaxPoolSize = LoaderMaxPoolSize,
            // Explicit so a future Npgsql default flip can't silently strand us pool-less.
            Pooling = true,
        };
        return builder.ConnectionString;
    }

    /// <summary>
    /// Self-host only. No-op unless the AppHost injects a Kestrel default-cert path (local dev
    /// uses the dev cert and skips). Preserves the retired <c>LoadLeafPfxPasswordFromStoreIfNeeded</c>'s
    /// throw semantics: PFX path set + no Postgres → throw; path set + row missing/empty → throw.
    /// </summary>
    private static void ApplyLeafPfxPasswordIfNeeded(
        IConfigurationBuilder cfg,
        IConfigurationRoot config,
        bool pgConnPresent,
        string? leafPfxPassword)
    {
        var pfxPath = config["Kestrel:Certificates:Default:Path"]
                      ?? Environment.GetEnvironmentVariable("ASPNETCORE_Kestrel__Certificates__Default__Path");
        if (string.IsNullOrWhiteSpace(pfxPath)) return;

        // If the operator pinned a password via env (the legacy path) prefer that over the
        // store lookup. Lets local dev or one-off recovery flows bypass the DB roundtrip.
        var existingPassword = config["Kestrel:Certificates:Default:Password"];
        if (!string.IsNullOrWhiteSpace(existingPassword)) return;

        if (!pgConnPresent)
        {
            throw new InvalidOperationException(
                "Kestrel default-cert path is set but OCTOCON_POSTGRES_CONNECTION is missing; " +
                "cannot fetch certs:leaf_pfx_password from internal.secrets.");
        }

        if (string.IsNullOrEmpty(leafPfxPassword))
        {
            throw new InvalidOperationException(
                "Row internal.secrets[certs:leaf_pfx_password] is missing or empty; " +
                "re-run the bootstrapper so SecretsPhase + DatabaseInitPhase seed it.");
        }

        cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kestrel:Certificates:Default:Password"] = leafPfxPassword,
        });
    }
}
