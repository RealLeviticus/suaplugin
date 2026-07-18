ALTER TABLE activation_requests
    ADD COLUMN IF NOT EXISTS area_categories TEXT NOT NULL DEFAULT '[]';

UPDATE activation_requests r
   SET area_categories = COALESCE((
       SELECT json_agg(json_build_object('Name', item.name, 'RaCategory', r.ra_category))::text
         FROM json_array_elements_text(r.area_names::json) AS item(name)
   ), '[]')
 WHERE area_categories = '[]';
