-- Bootstrap-only: creates the cluster-wide singleton keyspaces (`global`, `nam_nt`,
-- `dummy`) so global.schema_migrations can exist before any other migration runs and
-- so these keyspaces never need to be re-rendered per region. Applied unconditionally
-- on every startup (untracked); every statement is idempotent.
--
-- Placeholders:
--   {{GLOBAL_REPLICATION}}        Replication map for the global keyspace
--   {{NAM_NT_REPLICATION}}        Replication map for the nam_nt keyspace
--   {{TABLETS_CLAUSE}}            Tablets setting clause (empty on Cassandra, enabled on Scylla multi-DC)
--   {{TABLETS_CLAUSE_DISABLED}}   Tablets-disabled clause (Scylla only; empty on Cassandra)

CREATE KEYSPACE IF NOT EXISTS global WITH replication = {{GLOBAL_REPLICATION}} AND durable_writes = true{{TABLETS_CLAUSE}};
CREATE KEYSPACE IF NOT EXISTS nam_nt WITH replication = {{NAM_NT_REPLICATION}} AND durable_writes = true{{TABLETS_CLAUSE_DISABLED}};
CREATE KEYSPACE IF NOT EXISTS dummy WITH replication = {'class': 'SimpleStrategy', 'replication_factor': '1'} AND durable_writes = true{{TABLETS_CLAUSE_DISABLED}};

ALTER KEYSPACE global WITH replication = {{GLOBAL_REPLICATION}}{{TABLETS_CLAUSE}};
ALTER KEYSPACE nam_nt WITH replication = {{NAM_NT_REPLICATION}}{{TABLETS_CLAUSE_DISABLED}};
ALTER KEYSPACE dummy WITH replication = {'class': 'SimpleStrategy', 'replication_factor': '1'}{{TABLETS_CLAUSE_DISABLED}};
