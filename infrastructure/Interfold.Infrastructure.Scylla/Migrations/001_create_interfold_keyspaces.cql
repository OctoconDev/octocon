-- Regional keyspace creation template for Octocon.
--
-- This file is rendered and applied once per regional keyspace by
-- ScyllaMigrationService at startup. The cluster-wide singleton keyspaces
-- (global, nam_nt, dummy) live in 000_create_singleton_keyspaces.cql and are
-- created exactly once during bootstrap; this file only creates the per-region
-- {{KEYSPACE}}.
--
-- Placeholders:
--   {{KEYSPACE}}                Regional keyspace name (nam, eur, sam, sas, eas, ocn, gdpr)
--   {{KEYSPACE_REPLICATION}}    Replication map for the current regional keyspace
--   {{TABLETS_CLAUSE}}          Tablets setting clause (empty on Cassandra)

CREATE KEYSPACE IF NOT EXISTS {{KEYSPACE}} WITH replication = {{KEYSPACE_REPLICATION}} AND durable_writes = true{{TABLETS_CLAUSE}};

ALTER KEYSPACE {{KEYSPACE}} WITH replication = {{KEYSPACE_REPLICATION}}{{TABLETS_CLAUSE}};
