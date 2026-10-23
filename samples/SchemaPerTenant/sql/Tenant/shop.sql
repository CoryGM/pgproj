-- The shop's own schema. "shop" is the stand-in: in each shop's copy of the template it is that shop's schema (shop_acme, shop_globex, ...).
COMMENT ON SCHEMA shop IS 'One shop''s catalog, customers, and orders.';
GRANT USAGE ON SCHEMA shop TO PUBLIC;
ALTER DEFAULT PRIVILEGES IN SCHEMA shop GRANT SELECT ON TABLES TO PUBLIC;
