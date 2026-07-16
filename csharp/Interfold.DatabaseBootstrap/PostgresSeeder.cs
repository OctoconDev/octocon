using System.Security.Cryptography;

namespace Interfold.DatabaseBootstrap;

/// <summary>
/// Orchestrates the five Postgres seed steps end-to-end. Production callers (the bootstrapper)
/// pass <see cref="PostgresSeedOptions.ScrambleInitUserPassword"/> = true; test callers pass
/// false so the init credential stays usable on idempotent reruns.
/// </summary>
/// <remarks>
/// The orchestrator does NOT wait for the cluster to be ready — that's transport-specific
/// (compose-exec scans container logs for the normal-mode banner, the driver path opens a TCP
/// socket). Callers must wait + probe before invoking <see cref="BootstrapAsync"/>.
/// </remarks>
public static class PostgresSeeder
{
    // Alphabet matches the bootstrapper's SecretsPhase.PasswordAlphabet so the scramble
    // password "shape" looks identical to a generated one. 48 chars * log2(56) ≈ 279 bits of
    // entropy — well above any practical brute-force budget.
    private const string ScrambleAlphabet =
        "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

    /// <summary>
    /// Runs role creation, database creation, schema + grants, secrets upsert, and
    /// (optionally) the db_init password scramble. Idempotent: on a successful prior run,
    /// the app-role probe short-circuits and the method returns immediately.
    /// </summary>
    public static async Task BootstrapAsync(
        IPostgresExecutor executor,
        PostgresSeedOptions options,
        IDatabaseInitLogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        if (await AppRoleAlreadyConfiguredAsync(executor, options, ct).ConfigureAwait(false))
        {
            // Keyword "postgres already initialised" is what BootstrapIdempotenceTests grep for
            // when verifying a short-circuit on a second-bootstrap rerun. The DML-only suffix is
            // informational — it lets operators eyeball the cluster state in the phase log.
            logger.Info($"    postgres already initialised (state probe: app role '{options.AppUser}' is DML-only); skipping admin bootstrap");
            return;
        }

        logger.Info(
            $"    splitting cluster owner '{options.InitUser}' into app='{options.AppUser}' (DML-only) " +
            $"and admin='{options.AdminUser}' (superuser)...");

        // --- 1. CREATE / ALTER admin + app roles. Connect as the init user / postgres DB.
        await executor.ExecScriptAsync(
            options.InitUser, options.InitPassword, "postgres",
            PostgresSqlTemplates.BuildRolesSql(options), ct).ConfigureAwait(false);

        // --- 2. Ensure the application database exists and is owned by the admin role.
        // CREATE DATABASE cannot run inside a transaction, hence the simple-statement form.
        var dbExists = await executor.ExecScalarAsync(
            options.InitUser, options.InitPassword, "postgres",
            PostgresSqlTemplates.BuildDatabaseExistsProbeSql(options), ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(dbExists))
        {
            await executor.ExecScriptAsync(
                options.InitUser, options.InitPassword, "postgres",
                PostgresSqlTemplates.BuildCreateDatabaseSql(options), ct).ConfigureAwait(false);
        }
        else
        {
            await executor.ExecScriptAsync(
                options.InitUser, options.InitPassword, "postgres",
                PostgresSqlTemplates.BuildAlterDatabaseOwnerSql(options), ct).ConfigureAwait(false);
        }

        // --- 3. Schema + grants. Connect to the application database as the init user;
        // db_init is still a SUPERUSER at this point (any scramble happens in step 5).
        await executor.ExecScriptAsync(
            options.InitUser, options.InitPassword, options.DefaultDatabase,
            PostgresSqlTemplates.BuildSchemaSql(options), ct).ConfigureAwait(false);

        // --- 4. Seed internal.secrets. Connect as the admin role so the inserted rows are
        // owned by the same role that owns the table.
        foreach (var entry in SeedKeys.All)
        {
            var value = entry.ValueSelector(options);
            // Match DatabaseInitPhase: blank OAuth secrets etc. are deliberately skipped on
            // self-hosted deployments where the operator hasn't wired up the provider.
            if (string.IsNullOrEmpty(value)) continue;

            await executor.ExecScriptWithVarsAsync(
                options.AdminUser, options.AdminPassword, options.DefaultDatabase,
                PostgresSqlTemplates.UpsertSecretSql,
                [(PostgresSqlTemplates.UpsertKeyVar, entry.Key.Value),
                 (PostgresSqlTemplates.UpsertValueVar, value)],
                ct).ConfigureAwait(false);
        }

        // --- 5. Optionally scramble the init user's in-cluster password. Production callers
        // always do this; the test fixtures skip so their stable .env-equivalent value keeps
        // authenticating across idempotent reruns of WaitForResourcesAsync.
        if (options.ScrambleInitUserPassword)
        {
            var scrambled = RandomNumberGenerator.GetString(ScrambleAlphabet, 48);
            await executor.ExecScriptAsync(
                options.InitUser, options.InitPassword, "postgres",
                PostgresSqlTemplates.BuildScrambleInitUserSql(options, scrambled), ct).ConfigureAwait(false);

            logger.Info(
                $"    postgres bootstrapped: app='{options.AppUser}' (DML-only), " +
                $"admin='{options.AdminUser}' (superuser), {options.InitUser} scrambled");
        }
        else
        {
            logger.Info(
                $"    postgres bootstrapped: app='{options.AppUser}' (DML-only), " +
                $"admin='{options.AdminUser}' (superuser); {options.InitUser} password left intact " +
                "(test fixture flow)");
        }
    }

    private static async Task<bool> AppRoleAlreadyConfiguredAsync(
        IPostgresExecutor executor, PostgresSeedOptions options, CancellationToken ct)
    {
        // Probe as the app user against the application DB. If we can authenticate AND the
        // role's rolsuper is false, the prior seed pass completed cleanly. Any failure
        // (auth, db missing, network, transient transition) means "not yet seeded".
        try
        {
            var result = await executor.ExecScalarAsync(
                options.AppUser, options.AppPassword, options.DefaultDatabase,
                PostgresSqlTemplates.BuildAppRoleConfiguredProbeSql(options), ct).ConfigureAwait(false);
            return result == "1";
        }
        catch
        {
            return false;
        }
    }
}
