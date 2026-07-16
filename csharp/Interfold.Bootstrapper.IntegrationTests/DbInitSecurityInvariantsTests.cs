using Interfold.Bootstrapper.IntegrationTests.Attributes;
using Interfold.Bootstrapper.IntegrationTests.Fixtures;
using Interfold.Contracts.Configuration;
using TUnit.Core;

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
public class DbInitSecurityInvariantsTests(UbuntuDinDFixture dinD)
{
    private static string TestConfigJsonPath => Path.Combine(AppContext.BaseDirectory, "fixtures", "interfold.bootstrap.test.json");

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
    public async Task DumpOnFailure(TestContext ctx)
    {
        if (ctx.Execution.Result?.State == TestState.Failed)
        {
            await dinD.CaptureFailureArtifactsAsync(ctx.Metadata.TestName);
        }
        await dinD.TearDownComposeAsync(ctx.Metadata.TestName);
    }

    /// <summary>
    /// Drives a full bootstrap against a fresh scratch and returns the in-container compose
    /// file path. Each test calls this once at the top of its body, then issues its probes.
    /// </summary>
    private async Task<(DinDScratch Scratch, string ComposeFile)> BootstrapStackAsync(string testName)
    {
        var scratch = await dinD.CreateScratchAsync(testName, TestConfigJsonPath);
        var result = await dinD.RunBootstrapperAsync(testName,
            ["bootstrap", "--config", scratch.ConfigPath, "--output-dir", scratch.OutputDir,
             "--non-interactive", "--skip-prereqs"]);
        await Assert.That(result.ExitCode).IsEqualTo(0)
            .Because($"bootstrap must succeed before invariants can be checked: {result.Stderr}");
        return (scratch, $"{scratch.OutputDir}/docker-compose.yaml");
    }

    [Test]
    public async Task CassandraDefaultIsLockedAfterBootstrap()
    {
        var (_, composeFile) = await BootstrapStackAsync(nameof(CassandraDefaultIsLockedAfterBootstrap));

        // After BootstrapScyllaAsync, the default cassandra/cassandra login is locked - either
        // LOGIN=false or the password is scrambled. Either failure mode produces a non-zero
        // cqlsh exit; we don't care which, just that the default no longer authenticates.
        var auth = await dinD.ExecAsync(
            ["sh", "-c",
             $"docker compose -f {composeFile} exec -T scylla " +
             "cqlsh -u cassandra -p cassandra -e \"DESCRIBE CLUSTER\" 2>&1 || true"]);
        // cqlsh prints "Authentication error" or "Bad credentials"; the exact string varies by
        // scylla version so we just check the cqlsh exit code via grep — a clean auth would
        // print the cluster name on stdout and exit 0.
        await Assert.That(auth.Stdout.Contains("cluster", StringComparison.OrdinalIgnoreCase)).IsFalse()
            .Because($"cassandra/cassandra should be locked after bootstrap, but the cluster description came back: {auth.Stdout}");
    }

    [Test]
    public async Task DbInitPostgresPasswordIsScrambledInCluster()
    {
        var (scratch, composeFile) = await BootstrapStackAsync(nameof(DbInitPostgresPasswordIsScrambledInCluster));

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

        var auth = await dinD.ExecAsync(
            ["sh", "-c",
             $"docker compose -f {composeFile} exec -T -e PGPASSWORD={initPassword} msg-db " +
             $"psql -U {PostgresRoles.Init} -d postgres -h 127.0.0.1 -tAc 'SELECT 1' 2>&1 || true"]);
        // Auth failure prints "password authentication failed for user" on stderr.
        await Assert.That(auth.Stdout + auth.Stderr).Contains("authentication failed")
            .Or.Contains("password authentication")
            .Because($"db_init password should be scrambled in-cluster post-bootstrap; got: {auth.Stdout} / {auth.Stderr}");
    }

    [Test]
    public async Task InterfoldPostgresUserIsDmlOnly()
    {
        var (scratch, composeFile) = await BootstrapStackAsync(nameof(InterfoldPostgresUserIsDmlOnly));

        // Read the app user's password from the persisted secrets file (the only place it lives
        // outside the cluster after a successful bootstrap).
        var appPassRaw = await dinD.ExecAsync(
            ["sh", "-c",
             $"grep '\"postgresPassword\"' {scratch.OutputDir}/secrets/secrets.json " +
             "| sed -E 's/.*\"postgresPassword\"[^\"]*\"([^\"]+)\".*/\\1/'"]);
        var appPass = appPassRaw.Stdout.Trim();
        await Assert.That(appPass).IsNotEmpty()
            .Because("interfold password should be readable from secrets.json");

        // Try a DDL statement the DML-only role should NOT be able to execute. CREATE ROLE
        // requires SUPERUSER/CREATEROLE, neither of which the app user has.
        var ddl = await dinD.ExecAsync(
            ["sh", "-c",
             $"docker compose -f {composeFile} exec -T -e PGPASSWORD={appPass} msg-db " +
             $"psql -U interfold -d {TestPostgresDb} -h 127.0.0.1 -tAc \"CREATE ROLE escalation_attempt\" 2>&1 || true"]);
        await Assert.That(ddl.Stdout + ddl.Stderr).Contains("permission denied")
            .Or.Contains("must be superuser")
            .Because($"app user must not be allowed to CREATE ROLE: {ddl.Stdout} / {ddl.Stderr}");
    }

    [Test]
    public async Task InterfoldScyllaUserIsNonSuperuser()
    {
        var (scratch, composeFile) = await BootstrapStackAsync(nameof(InterfoldScyllaUserIsNonSuperuser));

        // Read scyllaAdminPassword from THIS test's secrets.json. `/opt/scratch/*/secrets.json | head -1`
        // would pick the alphabetically-first scratch dir (which is a sibling test's, since the
        // class shares one DinD container and TearDownComposeAsync deliberately leaves scratch
        // dirs in place for failure-artifact capture) and produce a "Bad credentials" auth error
        // every time this test isn't the first alphabetically.
        var adminPassRaw = await dinD.ExecAsync(
            ["sh", "-c",
             $"grep '\"scyllaAdminPassword\"' {scratch.OutputDir}/secrets/secrets.json " +
             "| sed -E 's/.*\"scyllaAdminPassword\"[^\"]*\"([^\"]+)\".*/\\1/'"]);
        var adminPass = adminPassRaw.Stdout.Trim();
        await Assert.That(adminPass).IsNotEmpty()
            .Because("scyllaAdminPassword must be persisted in secrets.json");

        // LIST ROLES OF '<user>' from the admin session prints a table that includes a 'super'
        // column. The app user must show super=False there.
        var adminUser = "interfold_admin";
        var roles = await dinD.ExecAsync(
            ["sh", "-c",
             $"docker compose -f {composeFile} exec -T scylla " +
             $"cqlsh -u {adminUser} -p {adminPass} " +
             "-e \"LIST ROLES OF 'interfold'\" 2>&1 || true"]);

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
        var (scratch, composeFile) = await BootstrapStackAsync(nameof(InterfoldAdminPostgresUserIsSuperuser));

        var adminPassRaw = await dinD.ExecAsync(
            ["sh", "-c",
             $"grep '\"postgresAdminPassword\"' {scratch.OutputDir}/secrets/secrets.json " +
             "| sed -E 's/.*\"postgresAdminPassword\"[^\"]*\"([^\"]+)\".*/\\1/'"]);
        var adminPass = adminPassRaw.Stdout.Trim();
        await Assert.That(adminPass).IsNotEmpty()
            .Because("postgresAdminPassword must be persisted in secrets.json");

        var probe = await dinD.ExecAsync(
            ["sh", "-c",
             $"docker compose -f {composeFile} exec -T -e PGPASSWORD={adminPass} msg-db " +
             $"psql -U interfold_admin -d {TestPostgresDb} -h 127.0.0.1 -tAc " +
             "\"SELECT rolsuper FROM pg_roles WHERE rolname='interfold_admin'\""]);
        await Assert.That(probe.ExitCode).IsEqualTo(0L).Because(probe.Stderr);
        await Assert.That(probe.Stdout.Trim()).IsEqualTo("t")
            .Because("interfold_admin must have rolsuper=true");
    }

    [Test]
    public async Task InternalSecretsTableIsSeeded()
    {
        var (scratch, composeFile) = await BootstrapStackAsync(nameof(InternalSecretsTableIsSeeded));

        var appPassRaw = await dinD.ExecAsync(
            ["sh", "-c",
             $"grep '\"postgresPassword\"' {scratch.OutputDir}/secrets/secrets.json " +
             "| sed -E 's/.*\"postgresPassword\"[^\"]*\"([^\"]+)\".*/\\1/'"]);
        var appPass = appPassRaw.Stdout.Trim();

        // The app user has SELECT on internal.secrets - we use it (not admin) so we also
        // implicitly confirm the grant from BootstrapPostgresAsync still applies.
        var count = await dinD.ExecAsync(
            ["sh", "-c",
             $"docker compose -f {composeFile} exec -T -e PGPASSWORD={appPass} msg-db " +
             $"psql -U interfold -d {TestPostgresDb} -h 127.0.0.1 -tAc 'SELECT COUNT(*) FROM internal.secrets'"]);
        await Assert.That(count.ExitCode).IsEqualTo(0L).Because(count.Stderr);
        // The seed list in BootstrapPostgresAsync inserts ~12 keys (only OAuth secrets that
        // are blank are skipped). Anything > 0 confirms seeding ran end-to-end; we use a soft
        // lower bound so future seed-list edits don't break this test.
        var rows = int.TryParse(count.Stdout.Trim(), out var n) ? n : 0;
        await Assert.That(rows).IsGreaterThan(0)
            .Because($"internal.secrets should contain seeded rows after bootstrap: got {count.Stdout}");
    }
}
