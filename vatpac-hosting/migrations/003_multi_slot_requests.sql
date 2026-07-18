ALTER TABLE activation_requests
    ADD COLUMN IF NOT EXISTS request_windows TEXT NOT NULL DEFAULT '[]';

UPDATE activation_requests
   SET request_windows = '["' ||
       to_char(start_utc::timestamptz AT TIME ZONE 'UTC', 'YYYYMMDDHH24MI') || '-' ||
       to_char(end_utc::timestamptz AT TIME ZONE 'UTC', 'YYYYMMDDHH24MI') || '"]'
 WHERE request_windows = '[]';
