CREATE TABLE platform.shops (
    slug text PRIMARY KEY CHECK (slug ~ '^[a-z][a-z0-9_]*$'),
    display_name text NOT NULL,
    plan text NOT NULL REFERENCES platform.plans (code),
    active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE platform.shops IS 'Every shop. Its schema is shop_<slug>. To list the tenants from here instead of tenants.csv, use "query": "SELECT slug FROM platform.shops WHERE active" in pgproj.json.';
