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

CREATE TABLE IF NOT EXISTS metadata (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
