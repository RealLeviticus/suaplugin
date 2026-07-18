ALTER TABLE areas ADD COLUMN line_pattern TEXT;
ALTER TABLE areas ADD COLUMN ra_category TEXT;

DELETE FROM metadata WHERE key = 'dataset_hash';
