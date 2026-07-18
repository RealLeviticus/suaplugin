CREATE TABLE IF NOT EXISTS areas (
    name TEXT PRIMARY KEY,
    type TEXT NOT NULL,
    floor INTEGER NOT NULL,
    ceiling INTEGER NOT NULL,
    daiw INTEGER NOT NULL DEFAULT 0,
    schedule TEXT NOT NULL DEFAULT '',
    active INTEGER NOT NULL DEFAULT 0,
    pre_active INTEGER NOT NULL DEFAULT 0,
    hidden INTEGER NOT NULL DEFAULT 0,
    manual INTEGER NOT NULL DEFAULT 0,
    h24_manual INTEGER NOT NULL DEFAULT 0,
    scheduled INTEGER NOT NULL DEFAULT 0,
    windows TEXT NOT NULL DEFAULT '[]',
    levels_edited INTEGER NOT NULL DEFAULT 0,
    line_pattern TEXT,
    ra_category TEXT,
    latitude DOUBLE PRECISION,
    longitude DOUBLE PRECISION,
    last_seen TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS desired_activations (
    name TEXT NOT NULL,
    source_type TEXT NOT NULL,
    source_id TEXT NOT NULL,
    h24 INTEGER NOT NULL DEFAULT 0,
    windows TEXT NOT NULL DEFAULT '[]',
    floor INTEGER,
    ceiling INTEGER,
    expires_at TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    controller_cid TEXT,
    line_pattern TEXT,
    ra_category TEXT,
    solar_mode TEXT,
    PRIMARY KEY (name, source_type, source_id)
);
CREATE INDEX IF NOT EXISTS idx_desired_expiry ON desired_activations(expires_at);

CREATE TABLE IF NOT EXISTS notams (
    id TEXT PRIMARY KEY,
    title TEXT NOT NULL,
    start_text TEXT NOT NULL DEFAULT '',
    end_text TEXT NOT NULL DEFAULT '',
    start_utc TEXT,
    end_utc TEXT,
    designators TEXT NOT NULL DEFAULT '[]',
    matched TEXT NOT NULL DEFAULT '[]',
    unmatched TEXT NOT NULL DEFAULT '[]',
    windows TEXT NOT NULL DEFAULT '[]',
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS notam_deactivations (notam_id TEXT PRIMARY KEY, created_at TEXT NOT NULL);

CREATE TABLE IF NOT EXISTS activation_requests (
    id TEXT PRIMARY KEY,
    area_name TEXT NOT NULL,
    area_names TEXT NOT NULL DEFAULT '[]',
    area_categories TEXT NOT NULL DEFAULT '[]',
    requester TEXT NOT NULL,
    contact_email TEXT NOT NULL DEFAULT '',
    start_utc TEXT NOT NULL,
    end_utc TEXT NOT NULL,
    request_windows TEXT NOT NULL DEFAULT '[]',
    notes TEXT NOT NULL DEFAULT '',
    ra_category TEXT NOT NULL DEFAULT 'RA1',
    vatsim_cid TEXT,
    vatsim_name TEXT,
    status TEXT NOT NULL DEFAULT 'pending',
    created_at TEXT NOT NULL,
    reviewed_at TEXT
);
CREATE INDEX IF NOT EXISTS idx_activation_requests_status_created ON activation_requests(status, created_at);

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
