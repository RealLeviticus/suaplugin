ALTER TABLE activation_requests ADD COLUMN ra_category TEXT NOT NULL DEFAULT 'RA1';
ALTER TABLE desired_activations ADD COLUMN ra_category TEXT;
