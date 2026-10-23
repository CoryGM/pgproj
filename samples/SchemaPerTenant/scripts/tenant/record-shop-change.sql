-- pgproj: run when=tenant-changed
-- Runs for a shop whose schema changed in this publish (a new shop too).
INSERT INTO platform.shop_events (shop, event) VALUES (:'tenant_id', 'schema updated in ' || current_schema());
