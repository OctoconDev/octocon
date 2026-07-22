using System.Security.Cryptography;

namespace Interfold.DatabaseBootstrap;

/// <summary>Runs the five Postgres seed steps end-to-end. Callers are responsible for
/// waiting until the cluster accepts connections; this orchestrator does not probe
/// readiness.</summary>
public static class PostgresSeeder
{
    // Matches SecretsPhase.PasswordAlphabet so the scramble password shape is
    // indistinguishable from a normal generated one. ~279 bits of entropy.
    private const string ScrambleAlphabet =
        "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

    /// <summary>Roles → database → schema → secrets → (optional) init-password scramble.
    /// Idempotent — a successful prior run makes this a no-op via the app-role probe.</summary>
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
            // "postgres already initialised" is grep-bait for BootstrapIdempotenceTests.
            logger.Info($"    postgres already initialised (state probe: app role '{options.AppUser}' is DML-only); skipping admin bootstrap");
            return;
        }

        logger.Info(
            $"    splitting cluster owner '{options.InitUser}' into app='{options.AppUser}' (DML-only) " +
            $"and admin='{options.AdminUser}' (superuser)...");

        // 1. CREATE/ALTER admin + app roles (init user, postgres DB).
        await executor.ExecScriptAsync(
            options.InitUser, options.InitPassword, "postgres",
            PostgresSqlTemplates.BuildRolesSql(options), ct).ConfigureAwait(false);

        // 2. App database owned by admin. CREATE DATABASE can't be in a transaction.
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

        // 3. Schema + grants on the app database, still as db_init (SUPERUSER until step 5).
        await executor.ExecScriptAsync(
            options.InitUser, options.InitPassword, options.DefaultDatabase,
            PostgresSqlTemplates.BuildSchemaSql(options), ct).ConfigureAwait(false);

        // 4. Seed internal.secrets as the admin role (co-owns the table).
        foreach (var entry in SeedKeys.All)
        {
            var value = entry.ValueSelector(options);
            if (string.IsNullOrEmpty(value)) continue;

            await executor.ExecScriptWithVarsAsync(
                options.AdminUser, options.AdminPassword, options.DefaultDatabase,
                PostgresSqlTemplates.UpsertSecretSql,
                [(PostgresSqlTemplates.UpsertKeyVar, entry.Key.Value),
                 (PostgresSqlTemplates.UpsertValueVar, value)],
                ct).ConfigureAwait(false);
        }

        // 5. Optional init-password scramble (production only).
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
        // Any exception (auth, missing DB, network) means "not yet seeded".
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
