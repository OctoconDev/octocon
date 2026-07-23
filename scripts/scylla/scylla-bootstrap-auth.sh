#!/bin/bash
set -euo pipefail

# ScyllaDB/Cassandra authentication bootstrap.
#
# This script runs ONCE to:
#   1. Create a dedicated admin account (<SCYLLA_USER>_admin) as superuser
#   2. Create the app user (SCYLLA_USER) as non-superuser (least privilege from the start)
#   3. Lock the default 'cassandra' account
#
# The API's migration service handles DDL and permission grants at startup using admin creds.
# Admin credentials are logged to stdout (visible in container logs).
#
# Required environment variables:
#   SCYLLA_USER           - App-level username (non-superuser)
#   SCYLLA_PASSWORD       - App-level password
#
# Optional environment variables:
#   SCYLLA_ADMIN_PASSWORD - Admin password (defaults to random if not set)
#
# Usage:
#   scylla-bootstrap-auth.sh <db-host>

DB_HOST="${1:?Usage: scylla-bootstrap-auth.sh <db-host>}"

# Default credentials for the built-in account
DEFAULT_USER="cassandra"
DEFAULT_PASSWORD="cassandra"
ADMIN_USER="${SCYLLA_USER}_admin"

# --- Guard: reject the well-known default password ---
if [ "$SCYLLA_PASSWORD" = "$DEFAULT_PASSWORD" ]; then
  echo "[auth-bootstrap] ========================================"
  echo "[auth-bootstrap] ERROR: SCYLLA_PASSWORD is set to the well-known default ('${DEFAULT_PASSWORD}')."
  echo "[auth-bootstrap]        This is not allowed — all accounts must use a non-default password."
  echo "[auth-bootstrap]"
  echo "[auth-bootstrap] To fix, set a secure password in user-secrets:"
  echo "[auth-bootstrap]   dotnet user-secrets set 'Parameters:scylla-password' '<your-password>'"
  echo "[auth-bootstrap]   (run from host/Interfold.AppHost)"
  echo "[auth-bootstrap] ========================================"
  exit 1
fi

echo "[auth-bootstrap] Waiting for CQL on ${DB_HOST}:9042..."
TIMEOUT=90
ELAPSED=0

# Try the app user first (works after previous bootstrap), then fall back to default.
CQL_READY=false
until [ "$CQL_READY" = "true" ]; do
  if cqlsh "$DB_HOST" -u "$SCYLLA_USER" -p "$SCYLLA_PASSWORD" -e "DESCRIBE CLUSTER" >/dev/null 2>&1; then
    CQL_READY=true
    echo "[auth-bootstrap] CQL is ready (connected as '${SCYLLA_USER}')."
    # If the app user can already connect, bootstrap was previously completed.
    echo "[auth-bootstrap] App user '${SCYLLA_USER}' can authenticate — bootstrap previously completed."
    echo "[auth-bootstrap] Bootstrap already complete (idempotent re-run)."
    exit 0
  elif cqlsh "$DB_HOST" -u "$DEFAULT_USER" -p "$DEFAULT_PASSWORD" -e "DESCRIBE CLUSTER" >/dev/null 2>&1; then
    CQL_READY=true
    echo "[auth-bootstrap] CQL is ready (connected as '${DEFAULT_USER}')."
  else
    sleep 2
    ELAPSED=$((ELAPSED + 2))
    if [ "$ELAPSED" -ge "$TIMEOUT" ]; then
      echo "[auth-bootstrap] ERROR: Timed out waiting for CQL (${TIMEOUT}s)"
      exit 1
    fi
  fi
done

# --- Step 1: Create dedicated admin superuser ---
ADMIN_PASS="${SCYLLA_ADMIN_PASSWORD:-$(head -c 32 /dev/urandom | base64 | tr -dc 'a-zA-Z0-9' | head -c 32)}"

ADMIN_EXISTING=$(cqlsh "$DB_HOST" -u "$DEFAULT_USER" -p "$DEFAULT_PASSWORD" \
  -e "LIST ROLES OF '${ADMIN_USER}';" 2>/dev/null || true)

if echo "$ADMIN_EXISTING" | grep -q "$ADMIN_USER"; then
  echo "[auth-bootstrap] Admin user '${ADMIN_USER}' already exists. Updating password..."
  cqlsh "$DB_HOST" -u "$DEFAULT_USER" -p "$DEFAULT_PASSWORD" -e \
    "ALTER ROLE '${ADMIN_USER}' WITH PASSWORD = '${ADMIN_PASS}';"
else
  echo "[auth-bootstrap] Creating admin superuser '${ADMIN_USER}'..."
  cqlsh "$DB_HOST" -u "$DEFAULT_USER" -p "$DEFAULT_PASSWORD" -e \
    "CREATE ROLE '${ADMIN_USER}' WITH PASSWORD = '${ADMIN_PASS}' AND SUPERUSER = true AND LOGIN = true;"
fi

# --- Step 2: Create the app user (non-superuser from the start) ---
EXISTING=$(cqlsh "$DB_HOST" -u "$DEFAULT_USER" -p "$DEFAULT_PASSWORD" \
  -e "LIST ROLES OF '${SCYLLA_USER}';" 2>/dev/null || true)

if echo "$EXISTING" | grep -q "$SCYLLA_USER"; then
  echo "[auth-bootstrap] App user '${SCYLLA_USER}' already exists."
  if [ "$SCYLLA_USER" = "$DEFAULT_USER" ]; then
    # The default 'cassandra' account can't demote itself. Use the admin to do it.
    echo "[auth-bootstrap] Updating '${SCYLLA_USER}' password and demoting via admin..."
    cqlsh "$DB_HOST" -u "$ADMIN_USER" -p "$ADMIN_PASS" -e \
      "ALTER ROLE '${SCYLLA_USER}' WITH PASSWORD = '${SCYLLA_PASSWORD}' AND SUPERUSER = false;"
  fi
else
  echo "[auth-bootstrap] Creating app user '${SCYLLA_USER}' (non-superuser)..."
  cqlsh "$DB_HOST" -u "$ADMIN_USER" -p "$ADMIN_PASS" -e \
    "CREATE ROLE IF NOT EXISTS '${SCYLLA_USER}' WITH PASSWORD = '${SCYLLA_PASSWORD}' AND SUPERUSER = false AND LOGIN = true;"
fi

# Verify the app user can connect
echo "[auth-bootstrap] Verifying '${SCYLLA_USER}' login..."
if ! cqlsh "$DB_HOST" -u "$SCYLLA_USER" -p "$SCYLLA_PASSWORD" -e "DESCRIBE CLUSTER" >/dev/null 2>&1; then
  echo "[auth-bootstrap] ERROR: Cannot authenticate as '${SCYLLA_USER}'. Aborting."
  exit 1
fi

# --- Step 3: Lock the default 'cassandra' account (unless SCYLLA_USER IS the default) ---
if [ "$SCYLLA_USER" = "$DEFAULT_USER" ]; then
  echo "[auth-bootstrap] SCYLLA_USER ('${SCYLLA_USER}') is the default account — skipping lock."
  echo "[auth-bootstrap] The app will continue using the default account with the configured password."
  CASSANDRA_RANDOM_PASS="(not locked - app user is default account)"
else
  CASSANDRA_RANDOM_PASS=$(head -c 32 /dev/urandom | base64 | tr -dc 'a-zA-Z0-9' | head -c 32)

  echo "[auth-bootstrap] Locking default '${DEFAULT_USER}' account..."
  cqlsh "$DB_HOST" -u "$ADMIN_USER" -p "$ADMIN_PASS" -e \
    "ALTER ROLE '${DEFAULT_USER}' WITH PASSWORD = '${CASSANDRA_RANDOM_PASS}' AND LOGIN = false;"
fi

# --- Summary (credentials visible in container logs only) ---
echo "[auth-bootstrap] ========================================"
echo "[auth-bootstrap] ADMIN ACCOUNT: ${ADMIN_USER}"
echo "[auth-bootstrap] ========================================"
if [ "$SCYLLA_USER" = "$DEFAULT_USER" ]; then
  echo "[auth-bootstrap] Default '${DEFAULT_USER}' account: ACTIVE (is app user)"
else
  echo "[auth-bootstrap] Default '${DEFAULT_USER}' account: LOCKED"
fi
echo "[auth-bootstrap] App user '${SCYLLA_USER}': non-superuser (DML-only, grants managed by API)"
echo "[auth-bootstrap] ========================================"
echo "[auth-bootstrap] Bootstrap complete."
echo "[auth-bootstrap]   Schema DDL: handled by API migration service using '${ADMIN_USER}'"
echo "[auth-bootstrap]   App runtime: '${SCYLLA_USER}' (non-superuser, DML only)"
