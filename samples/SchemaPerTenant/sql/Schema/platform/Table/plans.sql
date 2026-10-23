CREATE TABLE platform.plans (
    code text PRIMARY KEY,
    name text NOT NULL,
    monthly_price numeric(10, 2) NOT NULL,
    max_products integer
);
COMMENT ON TABLE platform.plans IS 'Subscription plans; a NULL max_products is unlimited.';
