using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Secrets;
using Npgsql;
using System.Security.Cryptography;

namespace Interfold.Api.Host.Services.Secrets;

/// <summary>Populates the API's <see cref="SecretsSnapshot"/> before
/// <c>WebApplicationBuilder.Build()</c>. Postgres branch: batched read on a bare Npgsql
/// connection (ISecretsStore isn't built yet). InMemory branch: reads
/// <c>OCTOCON_INMEMORY_SECRETS_SEED:*</c> env vars. Rewrites the pg connection string
/// via <see cref="WithDedicatedPoolIdentity"/> so parallel factory builds reuse a
/// dedicated pool instead of the fixture's pinned 5-slot app pool.</summary>
internal static class SecretsPreBuildLoader
{
    // Every row read by AuthenticationSecretsPostConfigure /
    // FirebaseClientSecretsPostConfigure / FcmSecretsPostConfigure — keep in sync.
    private static readonly SecretsStoreKey[] SnapshotKeys =
    [
        SecretsStoreKeys.OAuthGoogleClientSecret,
        SecretsStoreKeys.OAuthDiscordClientSecret,
        SecretsStoreKeys.OAuthAppleClientSecret,
        SecretsStoreKeys.EncryptionPepper,
        SecretsStoreKeys.AuthDeepLinkSecret,
        SecretsStoreKeys.AuthJwtRsa256PrivatePem,
        SecretsStoreKeys.AuthJwtEs256PrivatePem,

        SecretsStoreKeys.FirebaseClientAndroid,
        SecretsStoreKeys.FirebaseClientIos,
        SecretsStoreKeys.FirebaseClientWeb,

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

    private static Dictionary<SecretsStoreKey, string?> BuildInMemorySeedBuffer(IConfigurationRoot config) => new()
    {
        [SecretsStoreKeys.EncryptionPepper] = config[OctoconEnvKeys.InMemorySecretsSeedEncryptionPepper],
        [SecretsStoreKeys.AuthJwtEs256PrivatePem] = config[OctoconEnvKeys.InMemorySecretsSeedAuthJwtEs256PrivatePem],
        [SecretsStoreKeys.AuthDeepLinkSecret] = config[OctoconEnvKeys.InMemorySecretsSeedAuthDeepLinkSecret],
        [SecretsStoreKeys.AuthJwtRsa256PrivatePem] = config[OctoconEnvKeys.InMemorySecretsSeedAuthJwtRsa256PrivatePem],
    };

    /// <summary>Batched read of every snapshot row plus the leaf PFX password. Failures
    /// throw fail-fast.</summary>
    private static Dictionary<string, string?> FetchFromPostgres(string pgConn)
    {
        var rows = new Dictionary<string, string?>(StringComparer.Ordinal);
        var loaderConn = WithDedicatedPoolIdentity(pgConn);
        LoaderGate.Wait();
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
        finally
        {
            // Release only after Dispose has returned the physical connection to the pool,
            // otherwise the next waiter can Open() while this checkout is still live and
            // trip MaxPoolSize.
            LoaderGate.Release();
        }

        return rows;
    }

    // Distinct application_name → own Npgsql pool identity and easier pg_stat_activity triage.
    internal const string LoaderApplicationName = "octocon-secrets-preload";

    // Caps in-flight loader checkouts. Matched by LoaderGate so TUnit parallel factory
    // builds queue instead of exhausting the pool (Npgsql's default 15s wait then throws).
    internal const int LoaderMaxPoolSize = 10;

    // Open/checkout wait under a contended shared Postgres (solution-wide `dotnet test`).
    internal const int LoaderTimeoutSeconds = 60;

    private static readonly SemaphoreSlim LoaderGate = new(LoaderMaxPoolSize, LoaderMaxPoolSize);

    /// <summary>Rewrites <paramref name="pgConn"/> onto a dedicated pooled identity
    /// (distinct <c>Application Name</c>, bounded <c>Maximum Pool Size</c>). Overwrite is
    /// intentional — a fixture <c>Maximum Pool Size=5</c> would otherwise be inherited
    /// and the loader would contend with the app pool.</summary>
    internal static string WithDedicatedPoolIdentity(string pgConn)
    {
        var builder = new NpgsqlConnectionStringBuilder(pgConn)
        {
            ApplicationName = LoaderApplicationName,
            MaxPoolSize = LoaderMaxPoolSize,
            Timeout = LoaderTimeoutSeconds,
            Pooling = true,
        };
        return builder.ConnectionString;
    }

    /// <summary>Self-host only. No-op unless AppHost injects a Kestrel default-cert path.
    /// PFX path set + no Postgres → throw; path set + row missing/empty → throw.</summary>
    private static void ApplyLeafPfxPasswordIfNeeded(
        IConfigurationBuilder cfg,
        IConfigurationRoot config,
        bool pgConnPresent,
        string? leafPfxPassword)
    {
        var pfxPath = config["Kestrel:Certificates:Default:Path"]
                      ?? Environment.GetEnvironmentVariable("ASPNETCORE_Kestrel__Certificates__Default__Path");
        if (string.IsNullOrWhiteSpace(pfxPath)) return;

        // Operator-pinned env password wins over the store lookup for local dev / recovery.
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
