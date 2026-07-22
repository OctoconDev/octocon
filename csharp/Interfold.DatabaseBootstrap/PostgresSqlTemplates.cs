namespace Interfold.DatabaseBootstrap;

/// <summary>SQL bodies driven by <see cref="PostgresSeeder"/>. Every DO/IF block uses
/// <see cref="SqlEscape.Literal"/> + <c>format(... %I ... %L)</c> for defence-in-depth
/// even though the bootstrapper password alphabet excludes apostrophes.</summary>
public static class PostgresSqlTemplates
{
    /// <summary>Idempotent CREATE/ALTER ROLE for admin + app users. Run as the init user
    /// against the <c>postgres</c> database.</summary>
    public static string BuildRolesSql(PostgresSeedOptions o) =>
        $@"
DO $bootstrap$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{SqlEscape.Literal(o.AdminUser)}') THEN
        EXECUTE format('CREATE ROLE %I WITH SUPERUSER LOGIN PASSWORD %L', '{SqlEscape.Literal(o.AdminUser)}', '{SqlEscape.Literal(o.AdminPassword)}');
    ELSE
        EXECUTE format('ALTER ROLE %I WITH SUPERUSER LOGIN PASSWORD %L', '{SqlEscape.Literal(o.AdminUser)}', '{SqlEscape.Literal(o.AdminPassword)}');
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{SqlEscape.Literal(o.AppUser)}') THEN
        EXECUTE format('CREATE ROLE %I WITH NOSUPERUSER NOCREATEDB NOCREATEROLE LOGIN PASSWORD %L', '{SqlEscape.Literal(o.AppUser)}', '{SqlEscape.Literal(o.AppPassword)}');
    ELSE
        EXECUTE format('ALTER ROLE %I WITH NOSUPERUSER NOCREATEDB NOCREATEROLE LOGIN PASSWORD %L', '{SqlEscape.Literal(o.AppUser)}', '{SqlEscape.Literal(o.AppPassword)}');
    END IF;
END
$bootstrap$;";

    /// <summary>Returns <c>1</c> when <see cref="PostgresSeedOptions.DefaultDatabase"/> exists.</summary>
    public static string BuildDatabaseExistsProbeSql(PostgresSeedOptions o) =>
        $"SELECT 1 FROM pg_database WHERE datname = '{SqlEscape.Literal(o.DefaultDatabase)}'";

    /// <summary>Single CREATE DATABASE — cannot be transactional.</summary>
    public static string BuildCreateDatabaseSql(PostgresSeedOptions o) =>
        $"CREATE DATABASE \"{o.DefaultDatabase}\" OWNER \"{o.AdminUser}\";";

    /// <summary>ALTER DATABASE … OWNER TO for the rerun-against-existing-db case.</summary>
    public static string BuildAlterDatabaseOwnerSql(PostgresSeedOptions o) =>
        $"ALTER DATABASE \"{o.DefaultDatabase}\" OWNER TO \"{o.AdminUser}\";";

    /// <summary>Idempotent schema + grants. Run against the app database as the init user.</summary>
    public static string BuildSchemaSql(PostgresSeedOptions o) =>
        $@"
CREATE SCHEMA IF NOT EXISTS internal AUTHORIZATION ""{o.AdminUser}"";

CREATE TABLE IF NOT EXISTS internal.secrets (
    key          TEXT        PRIMARY KEY,
    value        TEXT        NOT NULL,
    created_by   TEXT        NOT NULL DEFAULT 'bootstrap',
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at   TIMESTAMPTZ,
    rotated_from TEXT
);
ALTER TABLE internal.secrets OWNER TO ""{o.AdminUser}"";

REVOKE ALL ON DATABASE ""{o.DefaultDatabase}"" FROM PUBLIC;
GRANT  CONNECT, TEMPORARY ON DATABASE ""{o.DefaultDatabase}"" TO ""{o.AppUser}"";

GRANT USAGE ON SCHEMA internal TO ""{o.AppUser}"";
GRANT SELECT ON internal.secrets TO ""{o.AppUser}"";

GRANT USAGE, CREATE ON SCHEMA public TO ""{o.AdminUser}"";
GRANT USAGE          ON SCHEMA public TO ""{o.AppUser}"";

GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES    IN SCHEMA public TO ""{o.AppUser}"";
GRANT USAGE,  SELECT, UPDATE          ON ALL SEQUENCES IN SCHEMA public TO ""{o.AppUser}"";

ALTER DEFAULT PRIVILEGES FOR ROLE ""{o.AdminUser}"" IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES    TO ""{o.AppUser}"";
ALTER DEFAULT PRIVILEGES FOR ROLE ""{o.AdminUser}"" IN SCHEMA public
    GRANT USAGE,  SELECT, UPDATE         ON SEQUENCES TO ""{o.AppUser}"";
";

    /// <summary>Upsert template for every <c>internal.secrets</c> row. Both transport
    /// adapters must bind these vars as safe quoted literals — psql via <c>:'name'</c>,
    /// Npgsql via parameter binding.</summary>
    public const string UpsertSecretSql = @"
INSERT INTO internal.secrets (key, value, created_by, updated_at)
VALUES (:'secret_key', :'secret_value', 'bootstrap', now())
ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now();";

    public const string UpsertKeyVar = "secret_key";
    public const string UpsertValueVar = "secret_value";

    /// <summary>Post-seed scramble of the init-user password. Production always runs;
    /// tests skip via <see cref="PostgresSeedOptions.ScrambleInitUserPassword"/>.</summary>
    public static string BuildScrambleInitUserSql(PostgresSeedOptions o, string scrambled) =>
        $"ALTER ROLE \"{o.InitUser}\" WITH PASSWORD '{SqlEscape.Literal(scrambled)}';";

    /// <summary>Returns <c>1</c> when the app user is already provisioned as non-superuser.
    /// Connect as the app user.</summary>
    public static string BuildAppRoleConfiguredProbeSql(PostgresSeedOptions o) =>
        $"SELECT 1 FROM pg_roles WHERE rolname = '{SqlEscape.Literal(o.AppUser)}' AND NOT rolsuper";
}
