-- Secrets table for storing OAuth credentials, encryption keys, etc.
-- Accessed at startup to patch configuration before the app accepts traffic.

CREATE SCHEMA IF NOT EXISTS internal;

CREATE TABLE IF NOT EXISTS internal.secrets (
    key         TEXT        PRIMARY KEY,
    value       TEXT        NOT NULL,
    created_by  TEXT        NOT NULL DEFAULT 'bootstrap',
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at  TIMESTAMPTZ,
    rotated_from TEXT
);
