CREATE TABLE products (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    sku text NOT NULL UNIQUE,
    name text NOT NULL,
    price money_amount NOT NULL,
    currency char(3) NOT NULL REFERENCES platform.currencies (code),
    active boolean NOT NULL DEFAULT true,
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ON products (lower(name)) WHERE active;
CREATE TRIGGER products_touch BEFORE UPDATE ON products FOR EACH ROW EXECUTE FUNCTION touch_updated_at();
COMMENT ON TABLE products IS 'The shop''s catalog.';
