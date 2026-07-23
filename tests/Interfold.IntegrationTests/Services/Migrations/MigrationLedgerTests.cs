using Cassandra;
using Interfold.Contracts;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Secrets;
using Interfold.Infrastructure.Postgres;
using Interfold.Infrastructure.Scylla;
using Interfold.IntegrationTests.TestServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Interfold.IntegrationTests.Services.Migrations;

/// <summary>
/// Exercises the migration ledger added to <see cref="PostgresMigrationService"/> and
/// <see cref="ScyllaMigrationService"/>. Verifies that the baseline session-bootstrap pass
/// recorded one row per applied migration, re-running the migrator is a no-op, and that
/// post-deploy content drift fails fast on the next start.
/// </summary>
/// <remarks>
/// Uses <see cref="ScyllaWebFactoryFixture"/> as the entry fixture so the
/// <see cref="RequiredFixtures.NeedScylla"/> toggle flips on for the session and the shared
/// <see cref="SharedDbFixture"/> brings up both Postgres and a single-node Scylla; we read
/// connection details off <see cref="ScyllaWebFactoryFixture.Aspire"/> rather than going
/// through the API factory. The tampering tests UPDATE the ledger row in place and restore
/// it inside a <c>try/finally</c> so the shared fixture's state is unchanged on exit.
/// </remarks>
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public sealed class MigrationLedgerTests(ScyllaWebFactoryFixture fixture) : BaseEndpointTest
{
    private SharedDbFixture SharedDb => fixture.Aspire;

    // --- Postgres ---

    [Test]
    public async Task Postgres_Ledger_HasOneRowPerEmbeddedMigration()
    {
        var migrationCount = CountEmbeddedResources(
            typeof(PostgresMigrationService).Assembly,
            "Interfold.Infrastructure.Postgres.Migrations.",
            ".sql");

        await using var conn = await OpenAdminPostgresConnectionAsync();

        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM internal.schema_migrations", conn);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());

        await Assert.That(count).IsEqualTo((long)migrationCount)
            .Because($"Expected one ledger row per embedded migration ({migrationCount}).");
    }

    [Test]
    [DependsOn(nameof(Postgres_Ledger_HasOneRowPerEmbeddedMigration))]
    public async Task Postgres_RerunningMigrations_IsANoOp()
    {
        var before = await SnapshotPostgresLedgerAsync();

        await PostgresMigrationService.MigrateAsync(
            BuildPersistenceConfig(),
            BuildPostgresSecretsStore(),
            NullLoggerFactory.Instance.CreateLogger<PostgresMigrationService>(),
            CancellationToken.None);

        var after = await SnapshotPostgresLedgerAsync();

        await Assert.That(after.Count).IsEqualTo(before.Count)
            .Because("Re-running migrations must not insert new ledger rows.");

        foreach (var (version, ts) in before)
        {
            await Assert.That(after.TryGetValue(version, out var afterTs)).IsTrue()
                .Because($"Migration '{version}' disappeared from the ledger after re-run.");
            await Assert.That(afterTs).IsEqualTo(ts)
                .Because($"Migration '{version}' applied_at changed across re-run.");
        }
    }

    [Test]
    [DependsOn(nameof(Postgres_RerunningMigrations_IsANoOp))]
    public async Task Postgres_ChecksumDriftThrows_AndPreservesLedger()
    {
        const string targetVersion = "000_create_secrets_table.sql";

        await using var conn = await OpenAdminPostgresConnectionAsync();

        var originalChecksum = await ReadChecksumAsync(conn, targetVersion);
        await Assert.That(originalChecksum).IsNotNull()
            .Because($"Ledger row for '{targetVersion}' was missing — baseline assertion needs to pass first.");

        const string fakeChecksum = "DEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEF";
        await UpdateChecksumAsync(conn, targetVersion, fakeChecksum);

        InvalidOperationException? thrown = null;
        try
        {
            await PostgresMigrationService.MigrateAsync(
                BuildPersistenceConfig(),
                BuildPostgresSecretsStore(),
                NullLoggerFactory.Instance.CreateLogger<PostgresMigrationService>(),
                CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }
        finally
        {
            await UpdateChecksumAsync(conn, targetVersion, originalChecksum!);
        }

        await Assert.That(thrown).IsNotNull()
            .Because("PostgresMigrationService should fail fast when a recorded checksum no longer matches the file.");
        await Assert.That(thrown!.Message).Contains("checksum");
        await Assert.That(thrown.Message).Contains(targetVersion);
    }

    // --- Scylla ---

    [Test]
    public async Task Scylla_Ledger_HasOneRowPerKeyspaceForTemplatedMigrations()
    {
        await Assert.That(SharedDb.ScyllaPort).IsNotNull()
            .Because("Scylla must be provisioned by the shared fixture for ledger tests.");

        using var session = await OpenScyllaSessionAsync();

        var rows = await session.ExecuteAsync(new SimpleStatement(
            "SELECT scope, version FROM global.schema_migrations"));

        var byVersion = rows
            .GroupBy(r => r.GetValue<string>("version"))
            .ToDictionary(g => g.Key, g => g.Select(r => r.GetValue<string>("scope")).ToHashSet());

        // SharedDbFixture provisions the single-node Scylla with IsSingleScyllaInstance=true,
        // so the templated migrations are applied to exactly one regional keyspace ("nam").
        await Assert.That(byVersion.TryGetValue("001_create_interfold_keyspaces.cql", out var keyspaceScopes)).IsTrue()
            .Because("Expected ledger to record the regional keyspace migration.");
        await Assert.That(keyspaceScopes!).Contains("nam");

        await Assert.That(byVersion.TryGetValue("002_create_interfold_schema.templated.cql", out var schemaScopes)).IsTrue()
            .Because("Expected ledger to record the templated schema migration.");
        await Assert.That(schemaScopes!).Contains("nam");
    }

    [Test]
    [DependsOn(nameof(Scylla_Ledger_HasOneRowPerKeyspaceForTemplatedMigrations))]
    public async Task Scylla_RerunningMigrations_IsANoOp()
    {
        await Assert.That(SharedDb.ScyllaPort).IsNotNull();

        var before = await SnapshotScyllaLedgerAsync();

        await ScyllaMigrationService.MigrateAsync(
            BuildPersistenceConfig(),
            BuildPostgresSecretsStore(),
            BuildScyllaResolver(),
            NullLoggerFactory.Instance.CreateLogger<ScyllaMigrationService>(),
            CancellationToken.None);

        var after = await SnapshotScyllaLedgerAsync();

        await Assert.That(after.Count).IsEqualTo(before.Count)
            .Because("Re-running Scylla migrations must not insert new ledger rows.");

        foreach (var (key, ts) in before)
        {
            await Assert.That(after.TryGetValue(key, out var afterTs)).IsTrue()
                .Because($"Ledger row {key} disappeared across re-run.");
            await Assert.That(afterTs).IsEqualTo(ts)
                .Because($"Ledger row {key} applied_at changed across re-run.");
        }
    }

    [Test]
    [DependsOn(nameof(Scylla_RerunningMigrations_IsANoOp))]
    public async Task Scylla_ChecksumDriftThrows_AndPreservesLedger()
    {
        await Assert.That(SharedDb.ScyllaPort).IsNotNull();

        const string targetScope = "nam";
        const string targetVersion = "002_create_interfold_schema.templated.cql";

        using var session = await OpenScyllaSessionAsync();

        var originalChecksum = await ReadScyllaChecksumAsync(session, targetScope, targetVersion);
        await Assert.That(originalChecksum).IsNotNull()
            .Because($"Ledger row ({targetScope}, {targetVersion}) was missing — baseline assertion needs to pass first.");

        const string fakeChecksum = "DEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEF";
        await UpdateScyllaChecksumAsync(session, targetScope, targetVersion, fakeChecksum);

        InvalidOperationException? thrown = null;
        try
        {
            await ScyllaMigrationService.MigrateAsync(
                BuildPersistenceConfig(),
                BuildPostgresSecretsStore(),
                BuildScyllaResolver(),
                NullLoggerFactory.Instance.CreateLogger<ScyllaMigrationService>(),
                CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }
        finally
        {
            await UpdateScyllaChecksumAsync(session, targetScope, targetVersion, originalChecksum!);
        }

        await Assert.That(thrown).IsNotNull()
            .Because("ScyllaMigrationService should fail fast when a recorded checksum no longer matches the template.");
        await Assert.That(thrown!.Message).Contains("checksum");
        await Assert.That(thrown.Message).Contains(targetVersion);
        await Assert.That(thrown.Message).Contains(targetScope);
    }

    // --- Helpers ---

    private PersistenceConfiguration BuildPersistenceConfig() => new()
    {
        Mode = PersistenceMode.ScyllaPostgres,
        PostgresConnectionString = SharedDb.PostgresConnectionString,
        IsSingleScyllaInstance = true,
        ScyllaKeyspace = ScyllaKeyspace.Nam,
    };

    private ISecretsStore BuildPostgresSecretsStore()
    {
        var connectionFactory = new PostgresConnectionFactory(Options.Create(BuildPersistenceConfig()));
        return new PostgresSecretsStore(connectionFactory);
    }

    /// <summary>
    /// Builds a resolver whose contact-point / port overrides point at the shared fixture's
    /// host-published Scylla endpoint. The <c>ScyllaKeyspace</c> comes from the shared
    /// <see cref="PersistenceConfiguration"/> — the resolver's <c>GetKeyspace</c> reads it
    /// straight off <see cref="PersistenceConfiguration.ScyllaKeyspace"/> now instead of
    /// re-parsing <c>OCTOCON_SCYLLA_KEYSPACE</c> from a throw-away IConfiguration.
    /// </summary>
    private IScyllaConfigResolver BuildScyllaResolver()
    {
        var overrides = new ScyllaOverrideOptions
        {
            ContactPoints = ["127.0.0.1"],
            Port = SharedDb.ScyllaPort!.Value,
        };
        return new ScyllaConfigResolver(
            BuildPostgresSecretsStore(),
            Options.Create(overrides),
            Options.Create(BuildPersistenceConfig()));
    }

    private async Task<ISession> OpenScyllaSessionAsync()
    {
        var sts = await ScyllaTestSession.OpenAsAdminAsync("127.0.0.1", SharedDb.ScyllaPort!.Value);
        return sts.Session;
    }

    private async Task<Dictionary<string, DateTime>> SnapshotPostgresLedgerAsync()
    {
        await using var conn = await OpenAdminPostgresConnectionAsync();

        await using var cmd = new NpgsqlCommand(
            "SELECT version, applied_at FROM internal.schema_migrations", conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        var snapshot = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            snapshot[reader.GetString(0)] = reader.GetDateTime(1);
        }
        return snapshot;
    }

    /// <summary>
    /// Opens a Postgres connection using the admin credentials stored in <c>internal.secrets</c>
    /// — the app user only has SELECT on <c>internal.secrets</c>, so any read/write against the
    /// ledger has to escalate to the same admin role <see cref="PostgresMigrationService"/>
    /// uses for DDL.
    /// </summary>
    private async Task<NpgsqlConnection> OpenAdminPostgresConnectionAsync()
    {
        var secrets = BuildPostgresSecretsStore();
        var adminUser = await secrets.GetAsync(SecretsStoreKeys.PostgresAdminUsername);
        var adminPassword = await secrets.GetAsync(SecretsStoreKeys.PostgresAdminPassword);

        var builder = new NpgsqlConnectionStringBuilder(SharedDb.PostgresConnectionString)
        {
            Username = adminUser,
            Password = adminPassword,
        };

        var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private async Task<Dictionary<(string Scope, string Version), DateTimeOffset>> SnapshotScyllaLedgerAsync()
    {
        using var session = await OpenScyllaSessionAsync();
        var rs = await session.ExecuteAsync(new SimpleStatement(
            "SELECT scope, version, applied_at FROM global.schema_migrations"));

        var snapshot = new Dictionary<(string, string), DateTimeOffset>();
        foreach (var row in rs)
        {
            var scope = row.GetValue<string>("scope");
            var version = row.GetValue<string>("version");
            var appliedAt = row.GetValue<DateTimeOffset>("applied_at");
            snapshot[(scope, version)] = appliedAt;
        }
        return snapshot;
    }

    private static async Task<string?> ReadChecksumAsync(NpgsqlConnection conn, string version)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT checksum FROM internal.schema_migrations WHERE version = @version", conn);
        cmd.Parameters.AddWithValue("version", version);
        return await cmd.ExecuteScalarAsync() as string;
    }

    private static async Task UpdateChecksumAsync(NpgsqlConnection conn, string version, string checksum)
    {
        await using var cmd = new NpgsqlCommand(
            "UPDATE internal.schema_migrations SET checksum = @checksum WHERE version = @version", conn);
        cmd.Parameters.AddWithValue("checksum", checksum);
        cmd.Parameters.AddWithValue("version", version);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ReadScyllaChecksumAsync(ISession session, string scope, string version)
    {
        var rs = await session.ExecuteAsync(new SimpleStatement(
            "SELECT checksum FROM global.schema_migrations WHERE scope = ? AND version = ?",
            scope, version));
        return rs.FirstOrDefault()?.GetValue<string>("checksum");
    }

    private static async Task UpdateScyllaChecksumAsync(ISession session, string scope, string version, string checksum)
    {
        await session.ExecuteAsync(new SimpleStatement(
            "UPDATE global.schema_migrations SET checksum = ? WHERE scope = ? AND version = ?",
            checksum, scope, version));
    }

    private static int CountEmbeddedResources(System.Reflection.Assembly assembly, string prefix, string suffix)
    {
        return assembly.GetManifestResourceNames()
            .Count(n => n.StartsWith(prefix, StringComparison.Ordinal)
                        && n.EndsWith(suffix, StringComparison.Ordinal));
    }
}
