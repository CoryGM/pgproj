CREATE TABLE orders (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    customer_id bigint NOT NULL REFERENCES customers (id),
    status order_status NOT NULL DEFAULT 'cart',
    currency char(3) NOT NULL REFERENCES platform.currencies (code),
    placed_at timestamptz,
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK (status = 'cart' OR placed_at IS NOT NULL)
);
CREATE INDEX ON orders (customer_id);
CREATE INDEX ON orders (status) WHERE status <> 'cart';
CREATE TRIGGER orders_touch BEFORE UPDATE ON orders FOR EACH ROW EXECUTE FUNCTION touch_updated_at();
COMMENT ON TABLE orders IS 'Orders, from cart to shipped.';
