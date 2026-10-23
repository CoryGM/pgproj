-- Seeds a new shop: its customer groups and a counter customer.
-- pgproj: run when=tenant-created
INSERT INTO :"tenant_schema".customer_groups (name, discount_percent) VALUES ('retail', 0), ('wholesale', 15);

INSERT INTO :"tenant_schema".customers (name, group_id)
SELECT 'Counter sale', id FROM :"tenant_schema".customer_groups WHERE name = 'retail';

INSERT INTO platform.shop_events (shop, event) VALUES (:'tenant_id', 'created');
