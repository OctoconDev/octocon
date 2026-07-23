-- =====================
-- Schema Template: Replace {{KEYSPACE}} with regional keyspace (nam, eur, ocn, eas, sam, sas, gdpr)
-- Applied by ScyllaMigrationService at startup, after 003_field_udt_timestamps.
--
-- Async-import job state. Two tables, kept consistent by the application layer:
--
--   * import_operations    — append-only history of every import that has ever been
--                            dispatched for a system. Rows transition through queued ->
--                            running -> (succeeded | failed). The clustering order keeps
--                            the most recent operation at the head of the partition so
--                            an operator can `SELECT * FROM import_operations WHERE
--                            system_id = '…' LIMIT 5` to see what just happened without
--                            paginating.
--
--   * active_import_by_system — single row per (system_id, kind) holding the operation_id
--                               currently in flight. Used as a per-system Cassandra LWT
--                               mutex: a new dispatcher tries `INSERT IF NOT EXISTS`; if
--                               another import is already active the row write fails
--                               cleanly and the dispatcher reuses the existing operation_id
--                               instead of starting a duplicate run. The row is removed
--                               (`DELETE IF operation_id = ?`) by the worker when the
--                               operation terminates, freeing the slot for the next click.
--
-- This pair is the load-bearing mutual-exclusion mechanism that prevents the duplicate-SP-import
-- bug from reappearing if a future caller (browser, retry middleware, another microservice)
-- happens to dispatch the same import twice in flight. Cassandra LWT (Paxos) gives us a real
-- linearizable check without needing a separate lock service.
-- =====================

CREATE TABLE IF NOT EXISTS {{KEYSPACE}}.import_operations (
    system_id text,
    operation_id timeuuid,
    kind text,
    status text,
    started_at timestamp,
    finished_at timestamp,
    alter_count int,
    error_code text,
    error_message text,
    idempotency_key text,
    PRIMARY KEY (system_id, operation_id)
) WITH CLUSTERING ORDER BY (operation_id DESC);

CREATE TABLE IF NOT EXISTS {{KEYSPACE}}.active_import_by_system (
    system_id text,
    kind text,
    operation_id timeuuid,
    started_at timestamp,
    PRIMARY KEY (system_id, kind)
);
