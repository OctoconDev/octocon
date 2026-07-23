CREATE TABLE IF NOT EXISTS octocon_idempotency (
    principal_id text NOT NULL,
    operation_id text NOT NULL,
    idempotency_key text NOT NULL,
    payload_hash text NOT NULL,
    outcome_hash text NOT NULL,
    outcome_payload text NULL,
    created_at timestamptz NOT NULL,
    PRIMARY KEY (principal_id, operation_id, idempotency_key)
);
