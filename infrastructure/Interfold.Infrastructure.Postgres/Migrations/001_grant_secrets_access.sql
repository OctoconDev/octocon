-- Grant app user read-only access to the internal schema and secrets table.
-- The bootstrap script creates the app user before migrations run.
-- Only SELECT is granted — writes require the admin account.
DO $$
DECLARE
    r RECORD;
BEGIN
    FOR r IN
        SELECT rolname FROM pg_roles
        WHERE rolcanlogin AND NOT rolsuper AND rolname NOT LIKE '%_admin'
    LOOP
        EXECUTE format('GRANT USAGE ON SCHEMA internal TO %I', r.rolname);
        EXECUTE format('GRANT SELECT ON internal.secrets TO %I', r.rolname);
    END LOOP;
END
$$;
