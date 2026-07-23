CREATE TABLE IF NOT EXISTS auth_tokens (
    jti TEXT PRIMARY KEY,
    system_id TEXT NOT NULL,
    issued_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    expires_at TIMESTAMPTZ NOT NULL,
    revoked_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_auth_tokens_system_id ON auth_tokens(system_id);
CREATE INDEX IF NOT EXISTS idx_auth_tokens_expires_at ON auth_tokens(expires_at);
CREATE INDEX IF NOT EXISTS idx_auth_tokens_revoked_at_not_null ON auth_tokens(revoked_at)
    WHERE revoked_at IS NOT NULL;
