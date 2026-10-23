CREATE TABLE customers (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email text UNIQUE,
    name text NOT NULL,
    group_id integer NOT NULL REFERENCES customer_groups (id),
    country char(2) REFERENCES platform.countries (code),
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ON customers (lower(name));
COMMENT ON TABLE customers IS 'The shop''s customers. A customer with no email buys at the counter.';
COMMENT ON COLUMN customers.country IS 'Where orders ship; a shared country (platform.countries).';
