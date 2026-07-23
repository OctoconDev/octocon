using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Cassandra;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Secrets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Scylla;

/// <summary>Applies embedded CQL migrations at startup under
/// <see cref="IHostedLifecycleService.StartingAsync"/>. Creates keyspaces, schema, and
/// DML grants under admin credentials from <see cref="ISecretsStore"/>. Handles
/// SimpleStrategy (single-node) and NetworkTopologyStrategy (multi-DC). Ledger is
/// <c>global.schema_migrations</c> keyed on <c>(scope, version)</c> + SHA-256 checksum;
/// singleton keyspaces (<c>global</c>, <c>nam_nt</c>, <c>dummy</c>) are bootstrapped
/// unconditionally because the ledger table lives in <c>global</c>. Templated
/// per-region migrations are tracked once per keyspace so a later region add re-runs
/// only against the new keyspace.</summary>
public sealed partial class ScyllaMigrationService(
    IOptions<PersistenceConfiguration> options,
    ISecretsStore secretsStore,
    IScyllaConfigResolver configResolver,
    ILogger<ScyllaMigrationService> logger) : IHostedLifecycleService
{
    // Derived from ScyllaKeyspace so the regional list can't drift from IRegionContext.
    private static readonly string[] RegionalKeyspaces =
        Enum.GetValues<Interfold.Shared.Contracts.Enums.ScyllaKeyspace>()
            .Select(Interfold.Shared.Contracts.Enums.EnumWire<Interfold.Shared.Contracts.Enums.ScyllaKeyspace>.ToWire)
            .ToArray();

    private static readonly string[] SingletonKeyspaces = ["global", "nam_nt", "dummy"];

    private const string LedgerKeyspace = "global";
    private const string LedgerTable = "schema_migrations";
    private const string LedgerTableFqn = $"{LedgerKeyspace}.{LedgerTable}";
    private const string SingletonsMigration = "000_create_singleton_keyspaces.cql";
    private const string KeyspacesMigration = "001_create_interfold_keyspaces.cql";
    private const string SchemaMigration = "002_create_interfold_schema.templated.cql";
    private const string FieldTimestampsMigration = "003_field_udt_timestamps.templated.cql";
    private const string ImportOperationsMigration = "004_import_operations.templated.cql";
    private const string ColorFixupMarkerMigration = "005_marker_color_fixup.templated.cql";
    private const string PrimaryFrontAddAlterMigration = "006_add_primary_front_alter.templated.cql";
    private const string GrantsVersion = "grants_v1";

    // Sequenced immediately after 006's ALTER; bump the checksum to force per-keyspace re-run.
    private const string PrimaryFrontBackfillVersion = "v1";
    private const string PrimaryFrontIntToAlterChecksum = "primary_front_int_to_alter_v1";

    // Bump GrantsVersion whenever this string changes so grants re-apply across all scopes.
    private const string GrantTemplate = "GRANT SELECT ON KEYSPACE {ks} TO {user};GRANT MODIFY ON KEYSPACE {ks} TO {user}";

    private string? _adminUsername;
    private string? _adminPassword;
    private string[]? _contactPoints;
    private string? _datacenter;
    private string? _appUsername;
    private string? _keyspace;
    private int _port;

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        _adminUsername = await secretsStore.GetAsync(SecretsStoreKeys.ScyllaAdminUsername, cancellationToken);
        _adminPassword = await secretsStore.GetAsync(SecretsStoreKeys.ScyllaAdminPassword, cancellationToken);

        if (string.IsNullOrWhiteSpace(_adminUsername) ||
            string.IsNullOrWhiteSpace(_adminPassword))
        {
            logger.LogInformation("[scylla-migrate] No admin credentials in secrets store — skipping migrations.");
            return;
        }

        _contactPoints = await configResolver.GetContactPointsAsync(cancellationToken);
        _datacenter = await configResolver.GetDatacenterAsync(cancellationToken);
        _appUsername = await configResolver.GetUsernameAsync(cancellationToken);
        _port = await configResolver.GetPortAsync(cancellationToken);

        // Keyspace is the per-node region identity — env-only, never store-shared.
        _keyspace = configResolver.GetKeyspace();

        logger.LogInformation("[scylla-migrate] Applying ScyllaDB schema migrations...");

        // Retry LIST ROLES — Cassandra's PasswordAuthenticator can accept anonymous
        // connections briefly after startup, before auth is enforced.
        var cluster = BuildCluster();
        var session = await cluster.ConnectAsync();

        for (var authAttempt = 0; authAttempt < 10; authAttempt++)
        {
            try
            {
                await session.ExecuteAsync(new SimpleStatement("LIST ROLES"));
                logger.LogInformation("[scylla-migrate] Authenticated as '{User}'.", _adminUsername);
                break;
            }
            catch (UnauthorizedException)
            {
                if (authAttempt >= 9) throw;
                logger.LogWarning("[scylla-migrate] Connection is anonymous — auth not ready, retrying in 3s (attempt {Attempt}/10)...",
                    authAttempt + 1);
                session.Dispose();
                await cluster.ShutdownAsync();
                await Task.Delay(3000);
                cluster = BuildCluster();
                session = await cluster.ConnectAsync();
            }
            catch (AuthenticationException)
            {
                if (authAttempt >= 9) throw;
                logger.LogWarning("[scylla-migrate] Auth rejected — admin role may not exist yet, retrying in 3s (attempt {Attempt}/10)...",
                    authAttempt + 1);
                session.Dispose();
                await cluster.ShutdownAsync();
                await Task.Delay(3000);
                cluster = BuildCluster();
                session = await cluster.ConnectAsync();
            }
        }

        try
        {
            var visibleDcs = await DiscoverDatacenters(session);
            var isScylla = await DetectScyllaDb(session);
            logger.LogInformation("[scylla-migrate] Visible datacenters: {DCs}, database: {Db}",
                string.Join(", ", visibleDcs), isScylla ? "ScyllaDB" : "Cassandra");

            // Untracked bootstrap — the ledger table lives in `global`, so `global` must exist first.
            await EnsureSingletonKeyspacesAsync(session, visibleDcs, isScylla);
            await EnsureLedgerAsync(session);
            var applied = await LoadAppliedAsync(session);

            await ApplyKeyspaces(session, visibleDcs, isScylla, applied);
            await ApplyTemplatedMigrationPerKeyspace(session, applied, SchemaMigration);
            await ApplyTemplatedMigrationPerKeyspace(session, applied, FieldTimestampsMigration);
            await ApplyTemplatedMigrationPerKeyspace(session, applied, ImportOperationsMigration);
            await ApplyTemplatedMigrationPerKeyspace(session, applied, ColorFixupMarkerMigration);

            // primary_front (int) -> primary_front_alter (smallint): same-name DROP+ADD of a
            // different type is server-rejected, RENAME is PK-only, so we use a new column
            // and backfill inline before traffic can bind to it.
            await ApplyTemplatedMigrationPerKeyspace(session, applied, PrimaryFrontAddAlterMigration);
            await BackfillPrimaryFrontIntToAlterAsync(session, applied, cancellationToken);

            await GrantPermissions(session, applied);
        }
        finally
        {
            session.Dispose();
            await cluster.ShutdownAsync();
        }

        logger.LogInformation("[scylla-migrate] All migrations applied.");

        _adminUsername = null;
        _adminPassword = null;
        logger.LogInformation("[scylla-migrate] Admin credentials cleared from memory.");
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Idempotent migration entry point. Exposed as a static so
    /// <c>SharedDbFixture</c> can migrate once per test session per backend instead of on
    /// every host build.</summary>
    public static Task MigrateAsync(
        PersistenceConfiguration options,
        ISecretsStore secretsStore,
        IScyllaConfigResolver configResolver,
        ILogger<ScyllaMigrationService> logger,
        CancellationToken cancellationToken)
    {
        var service = new ScyllaMigrationService(Options.Create(options), secretsStore, configResolver, logger);
        return service.StartingAsync(cancellationToken);
    }

    private Cluster BuildCluster() =>
        Cluster.Builder()
            .AddContactPoints(_contactPoints!)
            .WithPort(_port)
            .WithLoadBalancingPolicy(new DCAwareRoundRobinPolicy(_datacenter!))
            .WithCredentials(_adminUsername, _adminPassword)
            .WithQueryTimeout(30000)
            .WithSocketOptions(new SocketOptions()
                .SetConnectTimeoutMillis(15000)
                // 60s covers per-CREATE TABLE latency on contended hosts (e.g. self-hosted DinD).
                .SetReadTimeoutMillis(60000)
                .SetKeepAlive(true))
            .Build();

    // --- DC Discovery ---

    private async Task<HashSet<string>> DiscoverDatacenters(ISession session)
    {
        var dcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var localRows = await session.ExecuteAsync(new SimpleStatement("SELECT data_center FROM system.local"));
        foreach (var row in localRows)
        {
            var dc = row.GetValue<string>("data_center");
            if (!string.IsNullOrWhiteSpace(dc)) dcs.Add(dc.ToLowerInvariant());
        }

        var peerRows = await session.ExecuteAsync(new SimpleStatement("SELECT data_center FROM system.peers"));
        foreach (var row in peerRows)
        {
            var dc = row.GetValue<string>("data_center");
            if (!string.IsNullOrWhiteSpace(dc)) dcs.Add(dc.ToLowerInvariant());
        }

        dcs.IntersectWith(RegionalKeyspaces);
        return dcs;
    }

    // --- Replication Strategies (mirrors scylla-load-keyspaces.sh) ---

    private static bool IsMultiDc(HashSet<string> dcs) => dcs.Count > 1;

    private static string SimpleReplication() =>
        "{'class': 'SimpleStrategy', 'replication_factor': '1'}";

    private static string NtsReplication(HashSet<string> visibleDcs, params string[] preferredDcs)
    {
        var actualDcs = preferredDcs.Where(visibleDcs.Contains).ToArray();
        if (actualDcs.Length == 0) actualDcs = [visibleDcs.First()];
        var pairs = string.Join(", ", actualDcs.Select(dc => $"'{dc}': '1'"));
        return $"{{'class': 'NetworkTopologyStrategy', {pairs}}}";
    }

    private static string RegionalReplicationFor(string keyspace, HashSet<string> dcs)
    {
        if (!IsMultiDc(dcs)) return SimpleReplication();

        return keyspace switch
        {
            "nam" => NtsReplication(dcs, "nam", "eur", "sam"),
            "eur" => dcs.Contains("eur") ? NtsReplication(dcs, "nam", "eur", "sas") : NtsReplication(dcs, "nam", "sam", "sas"),
            "sam" => NtsReplication(dcs, "nam", "sam", "eas"),
            "sas" => dcs.Contains("sas") ? NtsReplication(dcs, "nam", "sas", "ocn") : NtsReplication(dcs, "nam", "sas", "gdpr"),
            "eas" => dcs.Contains("eas") ? NtsReplication(dcs, "nam", "eas", "ocn") : NtsReplication(dcs, "nam", "eas", "gdpr"),
            "ocn" => dcs.Contains("ocn") ? NtsReplication(dcs, "nam", "ocn", "gdpr") : NtsReplication(dcs, "nam", "ocn", "sas"),
            "gdpr" => dcs.Contains("gdpr") ? NtsReplication(dcs, "nam", "gdpr", "eur") : NtsReplication(dcs, "nam", "eur", "ocn"),
            _ => NtsReplication(dcs, "nam", "eur", "sam")
        };
    }

    private static string GlobalReplication(HashSet<string> dcs)
    {
        if (!IsMultiDc(dcs)) return SimpleReplication();

        // Global replicates to every available DC.
        var allDcs = new[] { "nam", "eur", "sam", "sas", "eas", "ocn", "gdpr" }
            .Where(dcs.Contains)
            .ToArray();
        return NtsReplication(dcs, allDcs);
    }

    private static string NamNtReplication(HashSet<string> dcs)
    {
        if (!IsMultiDc(dcs)) return SimpleReplication();
        return NtsReplication(dcs, "nam");
    }

    // --- Database Detection ---

    private static async Task<bool> DetectScyllaDb(ISession session)
    {
        try
        {
            var rs = await session.ExecuteAsync(new SimpleStatement("SELECT cluster_name FROM system.local"));
            // system_schema.scylla_tables is ScyllaDB-only.
            await session.ExecuteAsync(new SimpleStatement(
                "SELECT keyspace_name FROM system_schema.scylla_tables LIMIT 1"));
            return true;
        }
        catch
        {
            return false;
        }
    }

    // --- Singleton Keyspace Bootstrap (untracked) ---

    // Untracked: `global` must exist before the ledger table itself does.
    private async Task EnsureSingletonKeyspacesAsync(ISession session, HashSet<string> dcs, bool isScylla)
    {
        var cqlTemplate = GetEmbeddedResource(SingletonsMigration);
        var (tabletsClause, tabletsClauseDisabled) = ComputeTabletsClauses(dcs, isScylla);
        var rendered = cqlTemplate
            .Replace("{{GLOBAL_REPLICATION}}", GlobalReplication(dcs))
            .Replace("{{NAM_NT_REPLICATION}}", NamNtReplication(dcs))
            .Replace("{{TABLETS_CLAUSE_DISABLED}}", tabletsClauseDisabled)
            .Replace("{{TABLETS_CLAUSE}}", tabletsClause);

        logger.LogInformation("[scylla-migrate] Bootstrapping singleton keyspaces ({Singletons})...",
            string.Join(", ", SingletonKeyspaces));
        await ExecuteStatements(session, rendered);
    }

    // --- Keyspace Creation ---

    private async Task ApplyKeyspaces(
        ISession session,
        HashSet<string> dcs,
        bool isScylla,
        Dictionary<(string Scope, string Version), string> applied)
    {
        var cqlTemplate = GetEmbeddedResource(KeyspacesMigration);
        var checksum = ComputeChecksum(cqlTemplate);
        var (tabletsClause, _) = ComputeTabletsClauses(dcs, isScylla);

        var keyspaces = TargetKeyspaces();

        foreach (var keyspace in keyspaces)
        {
            if (ShouldSkip(applied, keyspace, KeyspacesMigration, checksum))
            {
                logger.LogDebug("[scylla-migrate] Skipping {Migration} for '{Keyspace}', already applied.",
                    KeyspacesMigration, keyspace);
                continue;
            }

            var regionalRepl = RegionalReplicationFor(keyspace, dcs);
            var rendered = cqlTemplate
                .Replace("{{KEYSPACE}}", keyspace)
                .Replace("{{KEYSPACE_REPLICATION}}", regionalRepl)
                .Replace("{{TABLETS_CLAUSE}}", tabletsClause);

            logger.LogInformation("[scylla-migrate] Creating keyspace '{Region}' (replication={Repl})...",
                keyspace, regionalRepl);
            var stopwatch = Stopwatch.StartNew();
            await ExecuteStatements(session, rendered);
            stopwatch.Stop();

            await RecordMigrationAsync(session, keyspace, KeyspacesMigration, checksum,
                (int)stopwatch.ElapsedMilliseconds);
        }
    }

    // --- Templated Per-Keyspace Migrations ---

    // Single seam for every keyspace-scoped migration — render {{KEYSPACE}}, skip via
    // ledger checksum, record via RecordMigrationAsync. Envelope-level changes (retry,
    // telemetry, error mapping) belong here.
    private async Task ApplyTemplatedMigrationPerKeyspace(
        ISession session,
        Dictionary<(string Scope, string Version), string> applied,
        string migrationFilename)
    {
        var cqlTemplate = GetEmbeddedResource(migrationFilename);
        var checksum = ComputeChecksum(cqlTemplate);

        var keyspaces = TargetKeyspaces();

        foreach (var keyspace in keyspaces)
        {
            if (ShouldSkip(applied, keyspace, migrationFilename, checksum))
            {
                logger.LogDebug("[scylla-migrate] Skipping {Migration} for '{Keyspace}', already applied.",
                    migrationFilename, keyspace);
                continue;
            }

            var rendered = cqlTemplate.Replace("{{KEYSPACE}}", keyspace);
            logger.LogInformation("[scylla-migrate] Applying {Migration} to keyspace '{Keyspace}'...",
                migrationFilename, keyspace);
            var stopwatch = Stopwatch.StartNew();
            await ExecuteStatements(session, rendered);
            stopwatch.Stop();

            await RecordMigrationAsync(session, keyspace, migrationFilename, checksum,
                (int)stopwatch.ElapsedMilliseconds);
        }
    }

    // --- Primary Front Backfill ---

    // Copies legacy int primary_front into the smallint sibling added by 006.
    // Ledger-guarded per keyspace + skips already-populated rows, so crash-restart resumes
    // on the null tail rather than repeating work.
    private async Task BackfillPrimaryFrontIntToAlterAsync(
        ISession session,
        Dictionary<(string Scope, string Version), string> applied,
        CancellationToken cancellationToken)
    {
        foreach (var keyspace in TargetKeyspaces())
        {
            var scope = $"primary_front_backfill_int_to_alter:{keyspace}";
            if (ShouldSkip(applied, scope, PrimaryFrontBackfillVersion, PrimaryFrontIntToAlterChecksum))
            {
                logger.LogDebug("[scylla-migrate] Skipping {Scope}, already applied.", scope);
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            var selectStmt = new SimpleStatement(
                $"SELECT id, primary_front, primary_front_alter FROM {keyspace}.users");
            selectStmt.SetPageSize(500);
            var rows = await session.ExecuteAsync(selectStmt);

            var copied = 0;
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (row.GetValue<short?>("primary_front_alter") is not null)
                    continue;

                var legacy = row.GetValue<int?>("primary_front");
                if (legacy is null)
                    continue;

                if (legacy is < short.MinValue or > short.MaxValue)
                {
                    throw new InvalidDataException(
                        $"users.primary_front value {legacy} is outside " +
                        $"[{short.MinValue}, {short.MaxValue}]; a legacy row escaped the " +
                        $"smallint invariant. Investigate before re-running the migration.");
                }

                var userId = row.GetValue<string>("id");
                var smallintValue = (short)legacy.Value;
                await session.ExecuteAsync(new SimpleStatement(
                    $"UPDATE {keyspace}.users SET primary_front_alter = ? WHERE id = ?",
                    smallintValue,
                    userId));
                copied++;
            }

            stopwatch.Stop();
            logger.LogInformation("[scylla-migrate] {Scope}: copied {Copied} row(s).", scope, copied);

            await RecordMigrationAsync(session, scope, PrimaryFrontBackfillVersion,
                PrimaryFrontIntToAlterChecksum, (int)stopwatch.ElapsedMilliseconds);
        }
    }

    // --- Permission Grants ---

    private async Task GrantPermissions(
        ISession session,
        Dictionary<(string Scope, string Version), string> applied)
    {
        var appUser = _appUsername;
        if (string.IsNullOrWhiteSpace(appUser))
        {
            logger.LogWarning("[scylla-migrate] No app user configured — skipping permission grants.");
            return;
        }

        if (string.Equals(appUser, _adminUsername, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("[scylla-migrate] App user is the admin user — skipping permission grants.");
            return;
        }

        logger.LogInformation("[scylla-migrate] Granting DML permissions to '{AppUser}'...", appUser);

        var grantChecksum = ComputeChecksum(GrantTemplate);
        var keyspaces = TargetKeyspaces()
            .Concat(SingletonKeyspaces)
            .ToArray();

        foreach (var keyspace in keyspaces)
        {
            var scope = $"grants:{keyspace}";
            if (ShouldSkip(applied, scope, GrantsVersion, grantChecksum))
            {
                logger.LogDebug("[scylla-migrate] Skipping grants for '{Keyspace}', already applied.", keyspace);
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            await GrantKeyspacePermissions(session, keyspace, appUser);
            stopwatch.Stop();

            await RecordMigrationAsync(session, scope, GrantsVersion, grantChecksum,
                (int)stopwatch.ElapsedMilliseconds);
        }
    }

    private async Task GrantKeyspacePermissions(ISession session, string keyspace, string user)
    {
        var grants = new[]
        {
            $"GRANT SELECT ON KEYSPACE {keyspace} TO '{user}'",
            $"GRANT MODIFY ON KEYSPACE {keyspace} TO '{user}'"
        };

        foreach (var stmt in grants)
        {
            try
            {
                await session.ExecuteAsync(new SimpleStatement(stmt));
            }
            catch (InvalidQueryException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                // Permission already granted — idempotent
            }
        }
    }

    // --- Ledger Helpers ---

    // Presumes `global` has already been bootstrapped by EnsureSingletonKeyspacesAsync.
    private static async Task EnsureLedgerAsync(ISession session)
    {
        var ddl = $$"""
                    CREATE TABLE IF NOT EXISTS {{LedgerTableFqn}} (
                        scope        text,
                        version      text,
                        checksum     text,
                        applied_at   timestamp,
                        duration_ms  int,
                        applied_by   text,
                        PRIMARY KEY (scope, version)
                    )
                    """;
        await session.ExecuteAsync(new SimpleStatement(ddl));
    }

    private static async Task<Dictionary<(string Scope, string Version), string>> LoadAppliedAsync(ISession session)
    {
        var applied = new Dictionary<(string, string), string>();
        var rs = await session.ExecuteAsync(new SimpleStatement(
            $"SELECT scope, version, checksum FROM {LedgerTableFqn}"));
        foreach (var row in rs)
        {
            var scope = row.GetValue<string>("scope");
            var version = row.GetValue<string>("version");
            var checksum = row.GetValue<string>("checksum");
            applied[(scope, version)] = checksum;
        }
        return applied;
    }

    // Throws on checksum drift so an edited-in-place migration cannot silently rebase.
    private static bool ShouldSkip(
        Dictionary<(string Scope, string Version), string> applied,
        string scope,
        string version,
        string checksum)
    {
        if (!applied.TryGetValue((scope, version), out var existing)) return false;
        if (string.Equals(existing, checksum, StringComparison.Ordinal)) return true;

        throw new InvalidOperationException(
            $"[scylla-migrate] Checksum mismatch for migration '{version}' (scope='{scope}'): " +
            $"recorded={existing}, actual={checksum}. Migration files must not be edited after " +
            $"they have been applied. Restore the original file or, if the change is intentional, " +
            $"manually update {LedgerTableFqn}.checksum for this row.");
    }

    private async Task RecordMigrationAsync(
        ISession session,
        string scope,
        string version,
        string checksum,
        int durationMs)
    {
        var appliedBy = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        var stmt = new SimpleStatement(
            $"INSERT INTO {LedgerTableFqn} (scope, version, checksum, applied_at, duration_ms, applied_by) " +
            "VALUES (?, ?, ?, ?, ?, ?) IF NOT EXISTS",
            scope, version, checksum, DateTimeOffset.UtcNow, durationMs, appliedBy);

        var rs = await session.ExecuteAsync(stmt);
        var row = rs.FirstOrDefault();
        // [applied]=false means a concurrent migrator inserted the same row first. Safe to ignore:
        // if the existing checksum differs we'll catch it on the next startup via ShouldSkip.
        if (row is not null && !row.GetValue<bool>("[applied]"))
        {
            logger.LogDebug("[scylla-migrate] Ledger row for ({Scope}, {Version}) already inserted by concurrent run.",
                scope, version);
        }
    }

    internal static string ComputeChecksum(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private string[] TargetKeyspaces() =>
        options.Value.IsSingleScyllaInstance
            ? [_keyspace!]
            : RegionalKeyspaces;

    private static (string TabletsClause, string TabletsClauseDisabled) ComputeTabletsClauses(
        HashSet<string> dcs, bool isScylla)
    {
        var tabletsClause = isScylla && IsMultiDc(dcs)
            ? " AND tablets = {'enabled': true}"
            : "";
        var tabletsClauseDisabled = isScylla
            ? " AND tablets = {'enabled': false}"
            : "";
        return (tabletsClause, tabletsClauseDisabled);
    }

    // --- CQL Execution ---

    private Task ExecuteStatements(ISession session, string cql)
        => ExecuteStatementsStatic(session, cql, logger);

    // Swallows AlreadyExistsException and column-added/removed InvalidQueryException so
    // a boot that crashed between an ALTER landing and its ledger row being written can
    // re-run on the next boot without exploding.
    private static async Task ExecuteStatementsStatic(ISession session, string cql, ILogger logger)
    {
        var statements = SplitCqlStatements(cql);
        foreach (var stmt in statements)
        {
            logger.LogDebug("[scylla-migrate] Executing: {Stmt}", stmt[..Math.Min(stmt.Length, 80)]);
            try
            {
                await session.ExecuteAsync(new SimpleStatement(stmt));
            }
            catch (AlreadyExistsException)
            {
                // Idempotent — keyspace/table/type/index already exists
            }
            catch (InvalidQueryException ex) when (IsColumnNotFound(ex))
            {
                // Idempotent — DROP {column} already applied on an earlier boot.
                logger.LogDebug("[scylla-migrate] Skipping DROP for missing column: {Message}", ex.Message);
            }
            catch (InvalidQueryException ex) when (IsColumnAlreadyExists(ex))
            {
                // Target Scylla version SyntaxErrors on `ADD IF NOT EXISTS`, so this
                // catch is how ALTER ... ADD migrations stay re-runnable across crashes.
                logger.LogDebug("[scylla-migrate] Skipping ADD for existing column: {Message}", ex.Message);
            }
        }
    }

    private static bool IsColumnNotFound(InvalidQueryException ex)
    {
        var msg = ex.Message;
        return msg.Contains("column", StringComparison.OrdinalIgnoreCase)
               && (msg.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
                   || msg.Contains("not found", StringComparison.OrdinalIgnoreCase)
                   || msg.Contains("undefined", StringComparison.OrdinalIgnoreCase)
                   || msg.Contains("unknown", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsColumnAlreadyExists(InvalidQueryException ex)
    {
        var msg = ex.Message;
        // Version-dependent wording; match intent, not the exact string.
        return (msg.Contains("column", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("conflicts with", StringComparison.OrdinalIgnoreCase))
               && (msg.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                   || msg.Contains("conflicts with an existing", StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> SplitCqlStatements(string cql)
    {
        return StatementSplitter().Split(cql)
            .Select(StripCommentLines)
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static string StripCommentLines(string chunk)
    {
        var lines = chunk.Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal) && l.Length > 0);
        return string.Join('\n', lines).Trim();
    }

    [GeneratedRegex(@";\s*$", RegexOptions.Multiline)]
    private static partial Regex StatementSplitter();

    private static string GetEmbeddedResource(string filename)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"Interfold.Infrastructure.Scylla.Migrations.{filename}";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
