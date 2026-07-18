ALTER TABLE activation_requests ADD COLUMN area_names TEXT NOT NULL DEFAULT '[]';

UPDATE activation_requests
   SET area_names = json_array(area_name)
 WHERE area_names = '[]';
