ALTER TABLE activation_requests ADD COLUMN area_categories TEXT NOT NULL DEFAULT '[]';

UPDATE activation_requests
   SET area_categories = (
       SELECT json_group_array(json_object('Name', value, 'RaCategory', activation_requests.ra_category))
         FROM json_each(activation_requests.area_names)
   )
 WHERE area_categories = '[]';
