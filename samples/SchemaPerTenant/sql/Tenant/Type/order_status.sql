CREATE TYPE order_status AS ENUM ('cart', 'placed', 'paid', 'shipped', 'cancelled');
COMMENT ON TYPE order_status IS 'Where an order is: a cart until it is placed.';
