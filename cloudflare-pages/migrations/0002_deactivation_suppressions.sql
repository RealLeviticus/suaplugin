CREATE TABLE IF NOT EXISTS default_deactivations (
    name TEXT PRIMARY KEY,
    created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS notam_deactivations (
    notam_id TEXT PRIMARY KEY,
    created_at TEXT NOT NULL
);
