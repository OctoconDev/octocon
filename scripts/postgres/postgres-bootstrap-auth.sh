#!/bin/bash
set -euo pipefail

# PostgreSQL authentication bootstrap.
#
# This script runs ONCE after the postgres container is healthy to:
#   1. Create the app user (PGUSER) as a non-superuser (DML-only, no CREATEDB)
#   2. Create a dedicated admin superuser (<PGUSER>_admin) with a random password
#   3. Create the application database owned by admin
#   4. Grant DML-only privileges (SELECT, INSERT, UPDATE, DELETE) to app user
#   5. Create the internal.secrets table and grant app user access
#   6. Seed secrets from environment variables into internal.secrets
#   7. Scramble the cluster owner (db_init) password
#
# The container starts with a disposable init superuser 'db_init' (cluster owner).
# We can't fully lock or demote the cluster owner in PostgreSQL, so instead we
# randomize its password. The admin password is generated randomly and stored
# directly in internal.secrets — it is never exposed via output or env vars.
#
# Required environment variables:
#   PGUSER       - App-level username (will be non-superuser)
#   PGPASSWORD   - App-level password
#
# Optional environment variables (secrets to seed):
#   OCTOCON_GOOGLE_OAUTH_CLIENT_SECRET
#   OCTOCON_DISCORD_OAUTH_CLIENT_SECRET
#   OCTOCON_ENCRYPTION_PEPPER
#   SCYLLA_USER           - Scylla app username (derives admin username as <user>_admin)
#   SCYLLA_ADMIN_PASSWORD - Scylla admin password (stored in secrets for API)
#
# The container's POSTGRES_PASSWORD (used by pg_init) is passed separately via
# PG_INIT_PASSWORD so we can connect as the cluster owner during bootstrap.
#
# Usage:
#   postgres-bootstrap-auth.sh <db-host> <db-name>

DB_HOST="${1:?Usage: postgres-bootstrap-auth.sh <db-host> <db-name>}"
DB_NAME="${2:?Usage: postgres-bootstrap-auth.sh <db-host> <db-name>}"

INIT_USER="db_init"
INIT_PASSWORD="${PG_INIT_PASSWORD:?PG_INIT_PASSWORD is required}"
APP_USER="${PGUSER:-postgres}"
APP_PASSWORD="${PGPASSWORD:?PGPASSWORD is required}"
ADMIN_USER="${APP_USER}_admin"

# --- Guard: reject the well-known default password ---
if [ "$APP_PASSWORD" = "postgres" ]; then
  echo "[pg-auth-bootstrap] ========================================"
  echo "[pg-auth-bootstrap] ERROR: PGPASSWORD is set to the well-known default ('postgres')."
  echo "[pg-auth-bootstrap]        This is not allowed — all accounts must use a non-default password."
  echo "[pg-auth-bootstrap]"
  echo "[pg-auth-bootstrap] To fix, set a secure password in user-secrets:"
  echo "[pg-auth-bootstrap]   dotnet user-secrets set 'Parameters:postgres-password' '<your-password>'"
  echo "[pg-auth-bootstrap]   (run from hosts/Interfold.AppHost)"
  echo "[pg-auth-bootstrap] ========================================"
  exit 1
fi

echo "[pg-auth-bootstrap] Waiting for PostgreSQL on ${DB_HOST}:5432..."
TIMEOUT=90
ELAPSED=0
until pg_isready -h "$DB_HOST" -U "$INIT_USER" -d postgres >/dev/null 2>&1; do
  sleep 2
  ELAPSED=$((ELAPSED + 2))
  if [ "$ELAPSED" -ge "$TIMEOUT" ]; then
    echo "[pg-auth-bootstrap] ERROR: Timed out waiting for PostgreSQL (${TIMEOUT}s)"
    exit 1
  fi
done
echo "[pg-auth-bootstrap] PostgreSQL is ready."

# Helper to run SQL as the app user (works after creation)
psql_as_app() {
  PGPASSWORD="$APP_PASSWORD" psql -v ON_ERROR_STOP=1 -h "$DB_HOST" -U "$APP_USER" -d postgres "$@"
}

# --- Idempotency: check if bootstrap already completed ---
# If the app user can connect AND is non-superuser, bootstrap already ran.
if psql_as_app -tAc "SELECT 1 FROM pg_roles WHERE rolname = '${APP_USER}' AND NOT rolsuper" 2>/dev/null | grep -q 1; then
  echo "[pg-auth-bootstrap] App user '${APP_USER}' exists as non-superuser — bootstrap previously completed."
  echo "[pg-auth-bootstrap] Bootstrap already complete (idempotent re-run)."
  exit 0
fi

# --- First-time bootstrap ---

# Helper to run SQL as the cluster owner (init superuser)
psql_as_init() {
  PGPASSWORD="$INIT_PASSWORD" psql -v ON_ERROR_STOP=1 -h "$DB_HOST" -U "$INIT_USER" -d postgres "$@"
}

# Verify we can connect as the init superuser
if ! psql_as_init -c "SELECT 1" >/dev/null 2>&1; then
  echo "[pg-auth-bootstrap] ERROR: Cannot authenticate as '${INIT_USER}'."
  echo "[pg-auth-bootstrap]        The cluster owner password may have been scrambled from a prior run."
  echo "[pg-auth-bootstrap]        Try deleting the 'msg_pgdata' volume for a fresh start."
  exit 1
fi

echo "[pg-auth-bootstrap] Connected as '${INIT_USER}' (cluster owner)."

# --- Step 1: Create the app user (non-superuser, DML-only) ---
echo "[pg-auth-bootstrap] Creating app user '${APP_USER}' (non-superuser)..."
psql_as_init -c "
DO \$\$
BEGIN
  IF EXISTS (SELECT FROM pg_roles WHERE rolname = '${APP_USER}') THEN
    ALTER ROLE \"${APP_USER}\" WITH PASSWORD '${APP_PASSWORD}' NOSUPERUSER LOGIN NOCREATEDB;
    RAISE NOTICE 'Role ${APP_USER} already exists, updated.';
  ELSE
    CREATE ROLE \"${APP_USER}\" WITH PASSWORD '${APP_PASSWORD}' NOSUPERUSER LOGIN NOCREATEDB;
  END IF;
END
\$\$;
"

# --- Step 2: Create dedicated admin superuser with random password ---
ADMIN_PASS=$(head -c 32 /dev/urandom | base64 | tr -dc 'a-zA-Z0-9' | head -c 32)

echo "[pg-auth-bootstrap] Creating admin superuser '${ADMIN_USER}'..."
psql_as_init -c "
DO \$\$
BEGIN
  IF EXISTS (SELECT FROM pg_roles WHERE rolname = '${ADMIN_USER}') THEN
    ALTER ROLE \"${ADMIN_USER}\" WITH PASSWORD '${ADMIN_PASS}' SUPERUSER LOGIN;
    RAISE NOTICE 'Role ${ADMIN_USER} already exists, rotated password.';
  ELSE
    CREATE ROLE \"${ADMIN_USER}\" WITH PASSWORD '${ADMIN_PASS}' SUPERUSER LOGIN;
  END IF;
END
\$\$;
"

# --- Step 3: Create the application database owned by admin ---
# The admin owns the database/schema (DDL). The app user gets DML-only.
echo "[pg-auth-bootstrap] Ensuring database '${DB_NAME}' exists..."
if ! psql_as_init -tAc "SELECT 1 FROM pg_database WHERE datname = '${DB_NAME}'" | grep -q 1; then
  psql_as_init -c "CREATE DATABASE \"${DB_NAME}\" OWNER \"${ADMIN_USER}\";"
  echo "[pg-auth-bootstrap] Database '${DB_NAME}' created (owner: ${ADMIN_USER})."
else
  echo "[pg-auth-bootstrap] Database '${DB_NAME}' already exists."
  psql_as_init -c "ALTER DATABASE \"${DB_NAME}\" OWNER TO \"${ADMIN_USER}\";" 2>/dev/null || true
fi

# --- Step 4: Grant DML-only privileges to app user ---
echo "[pg-auth-bootstrap] Granting DML privileges to '${APP_USER}' on '${DB_NAME}'..."

# Helper to run SQL against the application database as init
psql_as_init_db() {
  PGPASSWORD="$INIT_PASSWORD" psql -v ON_ERROR_STOP=1 -h "$DB_HOST" -U "$INIT_USER" -d "$DB_NAME" "$@"
}

# Grant CONNECT on the database
psql_as_init -c "GRANT CONNECT ON DATABASE \"${DB_NAME}\" TO \"${APP_USER}\";"

# Grant USAGE on public schema (no CREATE — can't make new tables)
psql_as_init_db -c "GRANT USAGE ON SCHEMA public TO \"${APP_USER}\";"

# Grant DML on all current and future tables in public schema
psql_as_init_db -c "GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO \"${APP_USER}\";"
psql_as_init_db -c "ALTER DEFAULT PRIVILEGES FOR ROLE \"${ADMIN_USER}\" IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO \"${APP_USER}\";"

# Grant USAGE on sequences (needed for serial/identity columns if any)
psql_as_init_db -c "GRANT USAGE ON ALL SEQUENCES IN SCHEMA public TO \"${APP_USER}\";"
psql_as_init_db -c "ALTER DEFAULT PRIVILEGES FOR ROLE \"${ADMIN_USER}\" IN SCHEMA public GRANT USAGE ON SEQUENCES TO \"${APP_USER}\";"

# --- Step 5: Create internal.secrets table ---
# Must happen here (before migrations) because PostgresMigrationService reads admin
# credentials from this table via the app user. The DDL and GRANT are also in
# Migrations/000 and 001 as idempotent insurance for non-Aspire environments.
echo "[pg-auth-bootstrap] Creating internal.secrets table..."

# Helper to run SQL against the application database as admin
psql_as_admin_db() {
  PGPASSWORD="$ADMIN_PASS" psql -v ON_ERROR_STOP=1 -h "$DB_HOST" -U "$ADMIN_USER" -d "$DB_NAME" "$@"
}

psql_as_admin_db -f /scripts/000_create_secrets_table.sql

# Grant app user read-only access to internal schema and secrets table
psql_as_admin_db -c "
GRANT USAGE ON SCHEMA internal TO \"${APP_USER}\";
GRANT SELECT ON internal.secrets TO \"${APP_USER}\";
"

# --- Step 6: Seed secrets from environment variables ---
echo "[pg-auth-bootstrap] Seeding secrets..."

upsert_secret() {
  local key="$1"
  local value="$2"
  if [ -z "$value" ]; then
    return
  fi
  # Use psql variables to avoid SQL injection from secret values.
  # Variable interpolation (:'var') only works when SQL is read from stdin, not with -c.
  psql_as_admin_db \
    -v "secret_key=$key" -v "secret_value=$value" <<'UPSERT_SQL'
    INSERT INTO internal.secrets (key, value, created_by, updated_at)
    VALUES (:'secret_key', :'secret_value', 'bootstrap', now())
    ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now();
UPSERT_SQL
  echo "[pg-auth-bootstrap]   Seeded: $key"
}

upsert_secret "oauth:google:client_secret" "${OCTOCON_GOOGLE_OAUTH_CLIENT_SECRET:-}"
upsert_secret "oauth:discord:client_secret" "${OCTOCON_DISCORD_OAUTH_CLIENT_SECRET:-}"
upsert_secret "encryption:pepper" "${OCTOCON_ENCRYPTION_PEPPER:-}"

# Seed admin credentials (these are the ONLY source for migration services)
upsert_secret "postgres:admin_username" "${ADMIN_USER}"
upsert_secret "postgres:admin_password" "${ADMIN_PASS}"
SCYLLA_ADMIN_USER="${SCYLLA_USER:-cassandra}_admin"
upsert_secret "scylla:admin_username" "${SCYLLA_ADMIN_USER}"
upsert_secret "scylla:admin_password" "${SCYLLA_ADMIN_PASSWORD:-}"

# Seed Scylla connection details (read by app services via ISecretsStore)
upsert_secret "scylla:contact_points" "${SCYLLA_CONTACT_POINTS:-127.0.0.1}"
upsert_secret "scylla:local_datacenter" "${SCYLLA_DATACENTER:-datacenter1}"
upsert_secret "scylla:username" "${SCYLLA_USER:-}"
upsert_secret "scylla:password" "${SCYLLA_PASSWORD:-}"
upsert_secret "scylla:keyspace" "${SCYLLA_KEYSPACE:-nam}"
upsert_secret "scylla:port" "${SCYLLA_PORT:-9042}"

# --- Step 7: Scramble the cluster owner password ---
INIT_SCRAMBLED_PASS=$(head -c 32 /dev/urandom | base64 | tr -dc 'a-zA-Z0-9' | head -c 32)
echo "[pg-auth-bootstrap] Scrambling cluster owner '${INIT_USER}' password..."
psql_as_init -c "ALTER ROLE \"${INIT_USER}\" WITH PASSWORD '${INIT_SCRAMBLED_PASS}';"

echo "[pg-auth-bootstrap] ========================================"
echo "[pg-auth-bootstrap] ADMIN ACCOUNT: ${ADMIN_USER}"
echo "[pg-auth-bootstrap] Cluster owner '${INIT_USER}': password scrambled"
echo "[pg-auth-bootstrap] App user '${APP_USER}': DML-only (SELECT/INSERT/UPDATE/DELETE)"
echo "[pg-auth-bootstrap] Secrets table: internal.secrets (seeded)"
echo "[pg-auth-bootstrap] ========================================"
echo "[pg-auth-bootstrap] Bootstrap complete."
