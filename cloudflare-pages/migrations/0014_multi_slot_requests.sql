ALTER TABLE activation_requests ADD COLUMN request_windows TEXT NOT NULL DEFAULT '[]';

UPDATE activation_requests
   SET request_windows = json_array(
       substr(replace(start_utc, '-', ''), 1, 8) || substr(start_utc, 12, 2) || substr(start_utc, 15, 2) || '-' ||
       substr(replace(end_utc, '-', ''), 1, 8) || substr(end_utc, 12, 2) || substr(end_utc, 15, 2)
   )
 WHERE request_windows = '[]';
