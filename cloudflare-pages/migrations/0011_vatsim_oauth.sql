CREATE TABLE IF NOT EXISTS oauth_states (
    state TEXT PRIMARY KEY,
    return_to TEXT NOT NULL,
    expires_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS auth_sessions (
    token_hash TEXT PRIMARY KEY,
    vatsim_cid TEXT NOT NULL,
    vatsim_name TEXT NOT NULL DEFAULT '',
    expires_at TEXT NOT NULL,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_auth_sessions_expiry ON auth_sessions(expires_at);

ALTER TABLE activation_requests ADD COLUMN vatsim_cid TEXT;
ALTER TABLE activation_requests ADD COLUMN vatsim_name TEXT;
