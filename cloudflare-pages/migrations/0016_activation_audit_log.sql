CREATE TABLE IF NOT EXISTS activation_audit_log (
    id TEXT PRIMARY KEY,
    area_name TEXT NOT NULL,
    event_type TEXT NOT NULL,
    source_type TEXT NOT NULL,
    actor_cid TEXT,
    actor_name TEXT,
    details TEXT NOT NULL DEFAULT '',
    occurred_at TEXT NOT NULL,
    created_at TEXT NOT NULL,
    event_key TEXT UNIQUE
);

CREATE INDEX IF NOT EXISTS idx_activation_audit_occurred ON activation_audit_log(occurred_at DESC);
CREATE INDEX IF NOT EXISTS idx_activation_audit_area ON activation_audit_log(area_name, occurred_at DESC);
