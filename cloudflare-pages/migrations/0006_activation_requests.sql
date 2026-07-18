CREATE TABLE IF NOT EXISTS activation_requests (
    id TEXT PRIMARY KEY,
    area_name TEXT NOT NULL,
    requester TEXT NOT NULL,
    start_utc TEXT NOT NULL,
    end_utc TEXT NOT NULL,
    notes TEXT NOT NULL DEFAULT '',
    status TEXT NOT NULL DEFAULT 'pending',
    created_at TEXT NOT NULL,
    reviewed_at TEXT
);

CREATE INDEX IF NOT EXISTS idx_activation_requests_status_created
    ON activation_requests(status, created_at);
