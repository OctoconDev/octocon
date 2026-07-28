using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;
using Interfold.Shared.Contracts.Configuration;

namespace Interfold.Bootstrapper.IntegrationTests;

/// <summary>
/// In-cluster security invariants for the running stack after a successful <c>bootstrap</c>.
/// Each test pins one specific contract from the database init phase:
///   <list type="bullet">
///     <item>Cassandra default account is locked.</item>
///     <item><c>db_init</c> password is scrambled in-cluster.</item>
///     <item>Application <c>interfold</c> user is DML-only on Postgres.</item>
///     <item>Application <c>interfold</c> user is not a superuser on Scylla.</item>
///     <item><c>interfold_admin</c> is a Postgres superuser.</item>
///     <item><c>internal.secrets</c> is seeded with the expected keys.</item>
///   </list>
/// </summary>
/// <remarks>
/// Each test runs a full bootstrap against its own scratch directory and a private host-port
/// window inside the shared DinD (see <see cref="DinDFixtureBase.CreateScratchAsync"/>'s port
/// allocator), so they execute concurrently with each other and with the other compose-up
/// tests in the assembly. The previous <c>ubuntu-compose-up</c> NotInParallel serialiser is no
/// longer needed.
/// </remarks>
[RequiresDocker]
[ClassDataSource<UbuntuDinDFixture>(Shared = SharedType.PerTestSession)]
[Explicit]
public class DbInitSecurityInvariantsTests(UbuntuDinDFixture dinD)
{

    /// <summary>
    /// Application database name the test bootstrap config asks the seeder to create. MUST stay
    /// in lockstep with the <c>postgresDatabase</c> field of <c>interfold.bootstrap.test.json</c>.
    /// Deliberately distinct from both the legacy <c>octocon</c> name and the production default
    /// <c>interfold</c> — every probe below selects against this name, so a regression that
    /// reintroduces a hardcoded literal anywhere in <c>DatabaseInitPhase</c> /
    /// <c>PostgresSeedOptions</c> / <c>PostgresSqlTemplates</c> would surface as a
    /// connect-or-permission-denied error here.
    /// </summary>
    private const string TestPostgresDb = "test_pg_db";

    [After(Test)]
    public Task DumpOnFailure(TestContext ctx) => DinDHookHelpers.DumpOnFailureAsync(dinD, ctx);

    [Test]
    public async Task CassandraDefaultIsLockedAfterBootstrap()
    {
        var (_, composeFile) = await dinD.BootstrapAsync(nameof(CassandraDefaultIsLockedAfterBootstrap), TestConfigPaths.DefaultConfig);

        // After BootstrapScyllaAsync, the default cassandra/cassandra login is locked - either
        // LOGIN=false or the password is scrambled. Either failure mode produces a non-zero
        // cqlsh exit; we don't care which, just that the default no longer authenticates.
        var auth = await dinD.CqlshAsync(composeFile, "cassandra", "cassandra", "\"DESCRIBE CLUSTER\"");
        // cqlsh prints "Authentication error" or "Bad credentials"; the exact string varies by
        // scylla version so we just check the cqlsh exit code via grep — a clean auth would
        // print the cluster name on stdout and exit 0.
        await Assert.That(auth.Stdout.Contains("cluster", StringComparison.OrdinalIgnoreCase)).IsFalse()
            .Because($"cassandra/cassandra should be locked after bootstrap, but the cluster description came back: {auth.Stdout}");
    }

    [Test]
    public async Task DbInitPostgresPasswordIsScrambledInCluster()
    {
        var (scratch, composeFile) = await dinD.BootstrapAsync(nameof(DbInitPostgresPasswordIsScrambledInCluster), TestConfigPaths.DefaultConfig);

        // The bootstrapper scrambles db_init's in-cluster password as the final step of
        // BootstrapPostgresAsync. The compose .env still ships the *initial* db_init password
        // (so volume-reset reruns work) - but the live cluster's password is something
        // operationally unrecoverable. Authing as db_init with the .env password must fail.
        var initPasswordRaw = await dinD.ExecAsync(
            ["sh", "-c",
             $"grep '^POSTGRES_INIT_PASSWORD=' {scratch.OutputDir}/.env | sed 's/^POSTGRES_INIT_PASSWORD=//'"]);
        var initPassword = initPasswordRaw.Stdout.Trim();
        await Assert.That(initPassword).IsNotEmpty()
            .Because("POSTGRES_INIT_PASSWORD must be present in the published .env");

        var auth = await dinD.PsqlAsync(
            composeFile, PostgresRoles.Init, "postgres", "'SELECT 1'",
            password: initPassword, softFail: true);
        // Auth failure prints "password authentication failed for user" on stderr.
        await Assert.That(auth.Stdout + auth.Stderr).Contains("authentication failed")
            .Or.Contains("password authentication")
            .Because($"db_init password should be scrambled in-cluster post-bootstrap; got: {auth.Stdout} / {auth.Stderr}");
    }

    [Test]
    public async Task InterfoldPostgresUserIsDmlOnly()
    {
        var (scratch, composeFile) = await dinD.BootstrapAsync(nameof(InterfoldPostgresUserIsDmlOnly), TestConfigPaths.DefaultConfig);

        // Read the app user's password from the persisted secrets file (the only place it lives
        // outside the cluster after a successful bootstrap). ReadSecretsFieldAsync parses the
        // file in-process via JsonDocument so it stays correct even if SecretsPhase.PersistAsync
        // switches to prettified/reordered JSON — the same rationale as the sibling call at L165.
        var appPass = await dinD.ReadSecretsFieldAsync(scratch, "postgresPassword");
        await Assert.That(appPass).IsNotEmpty()
            .Because("interfold password should be readable from secrets.json");

        // Try a DDL statement the DML-only role should NOT be able to execute. CREATE ROLE
        // requires SUPERUSER/CREATEROLE, neither of which the app user has.
        var ddl = await dinD.PsqlAsync(
            composeFile, "interfold", TestPostgresDb, "\"CREATE ROLE escalation_attempt\"",
            password: appPass, softFail: true);
        await Assert.That(ddl.Stdout + ddl.Stderr).Contains("permission denied")
            .Or.Contains("must be superuser")
            .Because($"app user must not be allowed to CREATE ROLE: {ddl.Stdout} / {ddl.Stderr}");
    }

    [Test]
    public async Task InterfoldScyllaUserIsNonSuperuser()
    {
        var (scratch, composeFile) = await dinD.BootstrapAsync(nameof(InterfoldScyllaUserIsNonSuperuser), TestConfigPaths.DefaultConfig);

        // Read scyllaAdminPassword from THIS test's secrets.json. Explicitly using scratch
        // (per-test dir) — `/opt/scratch/*/secrets.json | head -1` would pick the
        // alphabetically-first scratch dir (which is a sibling test's, since the class shares
        // one DinD container and TearDownComposeAsync deliberately leaves scratch dirs in place
        // for failure-artifact capture) and produce a "Bad credentials" auth error every time
        // this test isn't the first alphabetically.
        var adminPass = await dinD.ReadSecretsFieldAsync(scratch, "scyllaAdminPassword");
        await Assert.That(adminPass).IsNotEmpty()
            .Because("scyllaAdminPassword must be persisted in secrets.json");

        // LIST ROLES OF '<user>' from the admin session prints a table that includes a 'super'
        // column. The app user must show super=False there.
        var adminUser = "interfold_admin";
        var roles = await dinD.CqlshAsync(composeFile, adminUser, adminPass, "\"LIST ROLES OF 'interfold'\"");

        // cqlsh prints a fixed-width table: ` role | super | login | options`. We have to
        // look at the SUPER column specifically — a blanket `Contains("True")` matches the
        // LOGIN column too, which is legitimately True for the app user.
        await Assert.That(roles.Stdout).Contains("interfold")
            .Because($"LIST ROLES OF 'interfold' should return at least the role row: {roles.Stdout}");

        var interfoldRow = roles.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith("interfold ", StringComparison.Ordinal)
                                 || line.StartsWith("interfold|", StringComparison.Ordinal));
        await Assert.That(interfoldRow).IsNotNull()
            .Because($"failed to locate the 'interfold' role row in cqlsh output: {roles.Stdout}");

        // Columns are pipe-delimited with arbitrary whitespace padding. The second column is
        // `super` (the first column being the role name itself).
        var columns = interfoldRow!
            .Split('|', StringSplitOptions.TrimEntries)
            .Select(c => c.Trim())
            .ToArray();
        await Assert.That(columns.Length).IsGreaterThanOrEqualTo(2)
            .Because($"expected at least role|super columns; got '{interfoldRow}'");
        await Assert.That(columns[1]).IsEqualTo("False")
            .Because($"interfold scylla user must NOT be a superuser; super column = '{columns[1]}' in row '{interfoldRow}'");
    }

    [Test]
    public async Task InterfoldAdminPostgresUserIsSuperuser()
    {
        var (scratch, composeFile) = await dinD.BootstrapAsync(nameof(InterfoldAdminPostgresUserIsSuperuser), TestConfigPaths.DefaultConfig);

        var adminPass = await dinD.ReadSecretsFieldAsync(scratch, "postgresAdminPassword");
        await Assert.That(adminPass).IsNotEmpty()
            .Because("postgresAdminPassword must be persisted in secrets.json");

        var probe = await dinD.PsqlAsync(
            composeFile, "interfold_admin", TestPostgresDb,
            "\"SELECT rolsuper FROM pg_roles WHERE rolname='interfold_admin'\"",
            password: adminPass);
        await Assert.That(probe.ExitCode).IsEqualTo(0L).Because(probe.Stderr);
        await Assert.That(probe.Stdout.Trim()).IsEqualTo("t")
            .Because("interfold_admin must have rolsuper=true");
    }

    [Test]
    public async Task InternalSecretsTableIsSeeded()
    {
        var (scratch, composeFile) = await dinD.BootstrapAsync(nameof(InternalSecretsTableIsSeeded), TestConfigPaths.DefaultConfig);

        var appPass = await dinD.ReadSecretsFieldAsync(scratch, "postgresPassword");

        // The app user has SELECT on internal.secrets - we use it (not admin) so we also
        // implicitly confirm the grant from BootstrapPostgresAsync still applies.
        var count = await dinD.PsqlAsync(
            composeFile, "interfold", TestPostgresDb, "'SELECT COUNT(*) FROM internal.secrets'",
            password: appPass);
        await Assert.That(count.ExitCode).IsEqualTo(0L).Because(count.Stderr);
        // The seed list in BootstrapPostgresAsync inserts ~12 keys (only OAuth secrets that
        // are blank are skipped). Anything > 0 confirms seeding ran end-to-end; we use a soft
        // lower bound so future seed-list edits don't break this test.
        var rows = int.TryParse(count.Stdout.Trim(), out var n) ? n : 0;
        await Assert.That(rows).IsGreaterThan(0)
            .Because($"internal.secrets should contain seeded rows after bootstrap: got {count.Stdout}");
    }
}




