CREATE TABLE platform.shop_events (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    shop text NOT NULL,
    event text NOT NULL,
    at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ON platform.shop_events (shop, at);
COMMENT ON TABLE platform.shop_events IS 'What happened to each shop''s schema; written by the tenant scripts in scripts/tenant.';
