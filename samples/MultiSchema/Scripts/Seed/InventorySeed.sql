--  Warehouses
insert into inventory.warehouse (id, code, name, city, country_code, created_principal_id, updated_principal_id)
select md5('seed:warehouse:' || v.code)::uuid, v.code, v.name, v.city, 'US',
       '00000000-0000-0000-0000-000000000001'::uuid, '00000000-0000-0000-0000-000000000001'::uuid
from (values
    ('DAL-01', 'Dallas Distribution Center', 'Dallas'),
    ('SEA-01', 'Seattle Fulfilment Center',  'Seattle'),
    ('OLD-01', 'Retired Denver Warehouse',   'Denver')
) as v(code, name, city)
on conflict (id) do nothing;

update inventory.warehouse set is_active = false where code = 'OLD-01' and is_active;

--  Opening stock, received through the ledger so stock_level is filled in by the stock_movement_apply trigger.
--  Runs once per product and warehouse: the 'Opening stock' reason marks a movement already made.
insert into inventory.stock_movement (product_id, warehouse_id, movement_type, quantity_change, reason, created_principal_id)
select p.id, w.id, 'receipt', v.quantity, 'Opening stock', '00000000-0000-0000-0000-000000000001'::uuid
from (values
    ('TENT-2P-GRN', 'DAL-01', 40), ('TENT-2P-GRN', 'SEA-01', 25),
    ('TENT-4P-ORG', 'DAL-01', 15),
    ('BAG-20F',     'DAL-01', 60), ('BAG-20F',     'SEA-01', 45),
    ('BAG-0F',      'SEA-01', 12),
    ('PAD-INS',     'DAL-01', 80), ('PAD-INS',     'SEA-01', 50),
    ('STOVE-1',     'DAL-01', 100),
    ('POT-TI',      'DAL-01', 70),
    ('KIT-MESS',    'SEA-01', 90),
    ('LANT-LED',    'DAL-01', 55), ('LANT-LED',    'SEA-01', 30),
    ('HEAD-LAMP',   'DAL-01', 65),
    ('FILTER-WTR',  'SEA-01', 8),
    ('JKT-RAIN',    'DAL-01', 35), ('JKT-RAIN',    'SEA-01', 40),
    ('JKT-DOWN',    'SEA-01', 18),
    ('BOOT-HIKE',   'DAL-01', 30),
    ('SOCK-WOOL',   'DAL-01', 200), ('SOCK-WOOL',  'SEA-01', 150)
) as v(sku, warehouse_code, quantity)
join catalog.product as p on p.sku = v.sku
join inventory.warehouse as w on w.code = v.warehouse_code
where not exists (
    select 1 from inventory.stock_movement as sm
    where sm.product_id = p.id and sm.warehouse_id = w.id and sm.reason = 'Opening stock'
);

--  Reorder points
update inventory.stock_level as sl
set reorder_point = v.reorder_point
from (values
    ('TENT-2P-GRN', 20), ('TENT-4P-ORG', 10), ('BAG-20F', 30), ('BAG-0F', 10), ('PAD-INS', 40),
    ('STOVE-1', 40), ('POT-TI', 30), ('KIT-MESS', 30), ('LANT-LED', 25), ('HEAD-LAMP', 25),
    ('FILTER-WTR', 20), ('JKT-RAIN', 25), ('JKT-DOWN', 15), ('BOOT-HIKE', 20), ('SOCK-WOOL', 100)
) as v(sku, reorder_point)
join catalog.product as p on p.sku = v.sku
where sl.product_id = p.id
  and sl.reorder_point <> v.reorder_point;
