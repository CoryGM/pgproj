--  Product categories
--  Ids are derived from the code so the script can be run again without creating duplicates.
insert into catalog.category (id, parent_category_id, code, name, description, sort_order, created_principal_id, updated_principal_id)
select md5('seed:category:' || v.code)::uuid,
       case when v.parent_code is null then null else md5('seed:category:' || v.parent_code)::uuid end,
       v.code, v.name, v.description, v.sort_order,
       '00000000-0000-0000-0000-000000000001'::uuid, '00000000-0000-0000-0000-000000000001'::uuid
from (values
    ('camping',        null,      'Camping',        'Everything for a night outdoors',        1),
    ('tents',          'camping', 'Tents',          'Backpacking and family tents',           1),
    ('sleeping-bags',  'camping', 'Sleeping Bags',  'Bags and pads',                          2),
    ('cookware',       'camping', 'Cookware',       'Stoves, pots, and mess kits',            3),
    ('apparel',        null,      'Apparel',        'Clothing for the trail',                 2),
    ('jackets',        'apparel', 'Jackets',        'Rain and insulated jackets',             1),
    ('footwear',       'apparel', 'Footwear',       'Boots and socks',                        2)
) as v(code, parent_code, name, description, sort_order)
order by (v.parent_code is not null), v.sort_order
on conflict (id) do nothing;

--  Products (the price history rows are written by the product_record_price_history trigger)
insert into catalog.product (id, category_id, sku, name, description, unit_price, tax_rate, weight_grams, attributes, is_active, created_principal_id, updated_principal_id)
select md5('seed:product:' || v.sku)::uuid,
       md5('seed:category:' || v.category_code)::uuid,
       v.sku, v.name, v.description, v.unit_price, 0.0825, v.weight_grams, v.attributes::jsonb, v.is_active,
       '00000000-0000-0000-0000-000000000001'::uuid, '00000000-0000-0000-0000-000000000001'::uuid
from (values
    ('TENT-2P-GRN',  'tents',         'Trailhead 2-Person Tent',     'Lightweight two-person backpacking tent with a full-coverage rainfly.',  249.00, 2100,  '{"colour": "green", "capacity": 2}',  true),
    ('TENT-4P-ORG',  'tents',         'Basecamp 4-Person Tent',      'Roomy four-person tent with two doors and a vestibule.',                 389.00, 4800,  '{"colour": "orange", "capacity": 4}', true),
    ('BAG-20F',      'sleeping-bags', 'Summit 20F Sleeping Bag',     'Three-season synthetic sleeping bag rated to 20 degrees.',               129.00, 1300,  '{"rating_f": 20}',                    true),
    ('BAG-0F',       'sleeping-bags', 'Glacier 0F Sleeping Bag',     'Down winter sleeping bag rated to 0 degrees.',                           219.00, 1650,  '{"rating_f": 0}',                     true),
    ('PAD-INS',      'sleeping-bags', 'Insulated Sleeping Pad',      'Inflatable pad with an R-value of 4.',                                   59.95,  540,   '{"r_value": 4}',                      true),
    ('STOVE-1',      'cookware',      'Pocket Stove',                'Canister stove that boils a litre of water in three minutes.',           44.50,  85,    '{}',                                  true),
    ('POT-TI',       'cookware',      'Titanium Pot 900ml',          'Ultralight titanium pot with a folding handle.',                         38.00,  110,   '{"volume_ml": 900}',                  true),
    ('KIT-MESS',     'cookware',      'Camp Mess Kit',               'Plate, bowl, cup, and utensils that nest together.',                     24.99,  420,   '{}',                                  true),
    ('LANT-LED',     'camping',       'Camp Lantern',                'Rechargeable LED lantern, 300 lumens.',                                  29.00,  260,   '{"lumens": 300}',                     true),
    ('HEAD-LAMP',    'camping',       'Trail Headlamp',              'Rechargeable headlamp with a red night mode.',                           34.00,  90,    '{"lumens": 400}',                     true),
    ('FILTER-WTR',   'camping',       'Squeeze Water Filter',        'Hollow-fibre filter that removes bacteria and protozoa.',                39.95,  60,    '{}',                                  true),
    ('JKT-RAIN',     'jackets',       'Stormshield Rain Jacket',     'Waterproof breathable shell with a packable hood.',                      149.00, 380,   '{"colour": "navy"}',                  true),
    ('JKT-DOWN',     'jackets',       'Alpine Down Jacket',          'Warm 800-fill down jacket that packs into its own pocket.',              199.00, 420,   '{"colour": "charcoal"}',              true),
    ('BOOT-HIKE',    'footwear',      'Ridgeline Hiking Boots',      'Waterproof leather boots with a rugged outsole.',                        169.00, 1050,  '{"waterproof": true}',                true),
    ('SOCK-WOOL',    'footwear',      'Merino Hiking Socks',         'Cushioned merino wool socks.',                                           18.50,  95,    '{"material": "merino"}',              true),
    ('CHAIR-OLD',    'camping',       'Retired Camp Chair',          'Discontinued folding chair, kept for order history.',                    49.00,  1900,  '{}',                                  false)
) as v(sku, category_code, name, description, unit_price, weight_grams, attributes, is_active)
on conflict (id) do nothing;
