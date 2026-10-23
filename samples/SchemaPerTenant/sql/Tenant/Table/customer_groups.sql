CREATE TABLE customer_groups (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name text NOT NULL UNIQUE,
    discount_percent numeric(5, 2) NOT NULL DEFAULT 0 CHECK (discount_percent BETWEEN 0 AND 100)
);
COMMENT ON TABLE customer_groups IS 'Pricing groups. A new shop gets "retail" and "wholesale" (scripts/tenant/seed-new-shop.sql).';
