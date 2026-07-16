using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Secrets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Interfold.Infrastructure.Postgres;

/// <summary>
/// Applies embedded SQL migrations at startup using admin credentials from ISecretsStore.
/// Derives the admin connection string from the app connection + admin password from secrets.
/// Runs before the app accepts traffic (IHostedLifecycleService.StartingAsync).
/// </summary>
/// <remarks>
/// Each migration is tracked in <c>internal.schema_migrations</c> by version + SHA-256
/// checksum. On startup the runner reads the ledger, skips any file whose checksum matches
/// an existing row, and fails fast if a previously-applied file's content has drifted. The
/// migration body and the ledger insert are executed inside the same transaction so a partial
/// failure leaves no orphan ledger row behind. The ledger table itself is bootstrapped by the
/// runner (not a `.sql` migration) so the very first run can still record `000_*` correctly.
/// </remarks>
public sealed class PostgresMigrationService(
    IOptions<PersistenceConfiguration> options,
    ISecretsStore secretsStore,
    ILogger<PostgresMigrationService> logger) : IHostedLifecycleService
{
    // Advisory lock key used to serialize migrations across concurrent hosts sharing the same DB.
    private const long MigrationAdvisoryLockId = 8675309_2024_0001;

    public Task StartingAsync(CancellationToken cancellationToken) =>
        MigrateAsync(options.Value, secretsStore, logger, cancellationToken);

    /// <summary>
    /// Externally invocable entry point that runs the embedded SQL migrations using admin
    /// credentials from <paramref name="secretsStore"/>. The integration test
    /// <c>SharedDbFixture</c> calls this once per test session so the per-test
    /// <c>InterfoldWebApplicationFactory</c> doesn't need to rebuild migrations on every host.
    /// Idempotent: each migration is tracked in <c>internal.schema_migrations</c> and skipped
    /// on subsequent runs.
    /// </summary>
    public static async Task MigrateAsync(
        PersistenceConfiguration options,
        ISecretsStore secretsStore,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Build admin connection from app connection + admin credentials from secrets store
        var adminUsername = await secretsStore.GetAsync(SecretsStoreKeys.PostgresAdminUsername, cancellationToken);
        var adminPassword = await secretsStore.GetAsync(SecretsStoreKeys.PostgresAdminPassword, cancellationToken);
        if (string.IsNullOrWhiteSpace(adminUsername) ||
            string.IsNullOrWhiteSpace(adminPassword) ||
            string.IsNullOrWhiteSpace(options.PostgresConnectionString))
        {
            logger.LogInformation("[pg-migrate] No admin credentials in secrets store — skipping migrations.");
            return;
        }

        var connBuilder = new NpgsqlConnectionStringBuilder(options.PostgresConnectionString);
        connBuilder.Username = adminUsername;
        connBuilder.Password = adminPassword;
        var adminConnectionString = connBuilder.ConnectionString;

        logger.LogInformation("[pg-migrate] Applying PostgreSQL schema migrations...");

        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync(cancellationToken);

        // Acquire session-level advisory lock to prevent concurrent migration runs
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", connection))
        {
            lockCmd.Parameters.AddWithValue("key", MigrationAdvisoryLockId);
            await lockCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            await EnsureLedgerAsync(connection, cancellationToken);
            var applied = await LoadAppliedAsync(connection, cancellationToken);

            var migrations = GetMigrationScripts();
            var appliedBy = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
            var newlyApplied = 0;
            var skipped = 0;

            foreach (var (name, sql) in migrations)
            {
                var checksum = ComputeChecksum(sql);

                if (applied.TryGetValue(name, out var existingChecksum))
                {
                    if (!string.Equals(existingChecksum, checksum, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"[pg-migrate] Checksum mismatch for migration '{name}': " +
                            $"recorded={existingChecksum}, actual={checksum}. Migration files must not be " +
                            "edited after they have been applied. Restore the original file or, if the change " +
                            "is intentional, manually update internal.schema_migrations.checksum for this row.");
                    }

                    logger.LogDebug("[pg-migrate] Skipping {Migration}, already applied.", name);
                    skipped++;
                    continue;
                }

                logger.LogInformation("[pg-migrate] Applying: {Migration}", name);
                var stopwatch = Stopwatch.StartNew();

                await using var tx = await connection.BeginTransactionAsync(cancellationToken);
                try
                {
                    await using (var cmd = new NpgsqlCommand(sql, connection, tx))
                    {
                        await cmd.ExecuteNonQueryAsync(cancellationToken);
                    }

                    stopwatch.Stop();

                    await using (var insertCmd = new NpgsqlCommand(
                                     "INSERT INTO internal.schema_migrations " +
                                     "(version, checksum, duration_ms, applied_by) " +
                                     "VALUES (@version, @checksum, @duration_ms, @applied_by)",
                                     connection, tx))
                    {
                        insertCmd.Parameters.AddWithValue("version", name);
                        insertCmd.Parameters.AddWithValue("checksum", checksum);
                        insertCmd.Parameters.AddWithValue("duration_ms", (int)stopwatch.ElapsedMilliseconds);
                        insertCmd.Parameters.AddWithValue("applied_by", appliedBy);
                        await insertCmd.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await tx.CommitAsync(cancellationToken);
                    newlyApplied++;
                }
                catch
                {
                    await tx.RollbackAsync(cancellationToken);
                    throw;
                }
            }

            logger.LogInformation(
                "[pg-migrate] Applied {New} new migration(s), skipped {Skipped} already-applied.",
                newlyApplied, skipped);
        }
        finally
        {
            await using var unlockCmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
            unlockCmd.Parameters.AddWithValue("key", MigrationAdvisoryLockId);
            await unlockCmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static List<(string Name, string Sql)> GetMigrationScripts()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var prefix = "Interfold.Infrastructure.Postgres.Migrations.";

        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                return (Name: n[prefix.Length..], Sql: reader.ReadToEnd());
            })
            .ToList();
    }

    /// <summary>
    /// Ensures the <c>internal</c> schema and the <c>internal.schema_migrations</c> ledger
    /// table exist. Runs unconditionally on every startup — both statements are idempotent
    /// and the runner relies on the ledger being queryable before the per-file loop starts.
    /// </summary>
    private static async Task EnsureLedgerAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string ddl = """
                           CREATE SCHEMA IF NOT EXISTS internal;
                           CREATE TABLE IF NOT EXISTS internal.schema_migrations (
                               version      TEXT PRIMARY KEY,
                               checksum     TEXT NOT NULL,
                               applied_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
                               duration_ms  INTEGER NOT NULL,
                               applied_by   TEXT
                           );
                           """;

        await using var cmd = new NpgsqlCommand(ddl, connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Dictionary<string, string>> LoadAppliedAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var applied = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var cmd = new NpgsqlCommand(
            "SELECT version, checksum FROM internal.schema_migrations", connection);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            applied[reader.GetString(0)] = reader.GetString(1);
        }
        return applied;
    }

    internal static string ComputeChecksum(string sql)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
    }
}
