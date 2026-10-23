CREATE TABLE order_lines (
    order_id bigint NOT NULL REFERENCES orders (id) ON DELETE CASCADE,
    line_no smallint NOT NULL,
    product_id bigint NOT NULL REFERENCES products (id),
    quantity integer NOT NULL CHECK (quantity > 0),
    unit_price money_amount NOT NULL,
    PRIMARY KEY (order_id, line_no)
);
COMMENT ON TABLE order_lines IS 'What an order holds, at the price it was sold for.';
