namespace Interfold.DatabaseBootstrap;

/// <summary>CQL bodies driven by <see cref="ScyllaSeeder"/>.</summary>
public static class ScyllaCqlTemplates
{
    /// <summary>Cassandra/Scylla built-in superuser before lockdown.</summary>
    public const string DefaultUser = "cassandra";

    /// <summary>Default password for <see cref="DefaultUser"/> on a fresh data volume.</summary>
    public const string DefaultPassword = "cassandra";

    /// <summary>Admin superuser create. Must run as <see cref="DefaultUser"/> — Cassandra
    /// forbids altering your own superuser status.</summary>
    public static string BuildCreateAdminRoleCql(ScyllaSeedOptions o) =>
        $"CREATE ROLE IF NOT EXISTS '{SqlEscape.Literal(o.AdminUser)}' " +
        $"WITH PASSWORD = '{SqlEscape.Literal(o.AdminPassword)}' " +
        "AND SUPERUSER = true AND LOGIN = true";

    public static string BuildCreateAppRoleCql(ScyllaSeedOptions o) =>
        $"CREATE ROLE IF NOT EXISTS '{SqlEscape.Literal(o.AppUser)}' " +
        $"WITH PASSWORD = '{SqlEscape.Literal(o.AppPassword)}' " +
        "AND SUPERUSER = false AND LOGIN = true";

    /// <summary>Lockdown of the default user. Deliberately omits the SUPERUSER clause —
    /// cassandra is still one, and self-demotion errors.</summary>
    public static string BuildLockDefaultUserCql(string scrambledPassword) =>
        $"ALTER ROLE '{DefaultUser}' WITH PASSWORD = '{SqlEscape.Literal(scrambledPassword)}' AND LOGIN = false";

    /// <summary>Idempotency probe: LIST ROLES OF the admin returns the admin row iff
    /// role creation succeeded on a prior run.</summary>
    public static string BuildListAdminRoleCql(ScyllaSeedOptions o) =>
        $"LIST ROLES OF '{SqlEscape.Literal(o.AdminUser)}'";
}
