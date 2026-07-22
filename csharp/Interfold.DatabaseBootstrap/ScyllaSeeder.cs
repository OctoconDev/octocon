using System.Security.Cryptography;

namespace Interfold.DatabaseBootstrap;

/// <summary>Mints the admin + app roles then locks the default <c>cassandra</c> account.
/// Every statement runs as the built-in <c>cassandra</c> session because Cassandra
/// forbids a role from altering its own superuser status.</summary>
public static class ScyllaSeeder
{
    private const string ScrambleAlphabet =
        "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

    /// <summary>Idempotent — short-circuits when the admin role already exists.</summary>
    public static async Task BootstrapAsync(
        IScyllaExecutor executor,
        ScyllaSeedOptions options,
        IDatabaseInitLogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        if (await AdminRoleAlreadyConfiguredAsync(executor, options, ct).ConfigureAwait(false))
        {
            // "scylla already initialised" is grep-bait for BootstrapIdempotenceTests.
            logger.Info($"    scylla already initialised (state probe: admin role '{options.AdminUser}' present); skipping admin bootstrap");
            return;
        }

        logger.Info(
            $"    creating scylla roles: app='{options.AppUser}' (DML-only), " +
            $"admin='{options.AdminUser}' (superuser), locking default cassandra...");

        await executor.ExecCqlAsync(
            ScyllaCqlTemplates.DefaultUser, ScyllaCqlTemplates.DefaultPassword,
            ScyllaCqlTemplates.BuildCreateAdminRoleCql(options), ct).ConfigureAwait(false);

        await executor.ExecCqlAsync(
            ScyllaCqlTemplates.DefaultUser, ScyllaCqlTemplates.DefaultPassword,
            ScyllaCqlTemplates.BuildCreateAppRoleCql(options), ct).ConfigureAwait(false);

        if (options.LockDefaultCassandra
            && !string.Equals(options.AppUser, ScyllaCqlTemplates.DefaultUser, StringComparison.Ordinal)
            && !string.Equals(options.AdminUser, ScyllaCqlTemplates.DefaultUser, StringComparison.Ordinal))
        {
            // LOGIN=false is the real protection; the throwaway password is belt+braces.
            var scrambled = RandomNumberGenerator.GetString(ScrambleAlphabet, 48);
            await executor.ExecCqlAsync(
                ScyllaCqlTemplates.DefaultUser, ScyllaCqlTemplates.DefaultPassword,
                ScyllaCqlTemplates.BuildLockDefaultUserCql(scrambled), ct).ConfigureAwait(false);
        }

        logger.Info(
            $"    scylla bootstrapped: app='{options.AppUser}' (non-superuser), " +
            $"admin='{options.AdminUser}' (superuser), default cassandra " +
            (options.LockDefaultCassandra ? "locked" : "left intact"));
    }

    private static async Task<bool> AdminRoleAlreadyConfiguredAsync(
        IScyllaExecutor executor, ScyllaSeedOptions options, CancellationToken ct)
    {
        // Default session works pre-lockdown; post-lockdown we fall back to admin creds.
        var asDefault = await executor.TryExecCqlAsync(
            ScyllaCqlTemplates.DefaultUser, ScyllaCqlTemplates.DefaultPassword,
            ScyllaCqlTemplates.BuildListAdminRoleCql(options), ct).ConfigureAwait(false);
        if (asDefault.Succeeded && asDefault.Output.Contains(options.AdminUser, StringComparison.Ordinal))
        {
            return true;
        }

        var asAdmin = await executor.TryExecCqlAsync(
            options.AdminUser, options.AdminPassword,
            ScyllaCqlTemplates.BuildListAdminRoleCql(options), ct).ConfigureAwait(false);
        return asAdmin.Succeeded;
    }
}
