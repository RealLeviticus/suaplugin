ALTER TABLE areas ADD COLUMN latitude REAL;
ALTER TABLE areas ADD COLUMN longitude REAL;
ALTER TABLE desired_activations ADD COLUMN solar_mode TEXT;
DELETE FROM metadata WHERE key = 'dataset_hash';
