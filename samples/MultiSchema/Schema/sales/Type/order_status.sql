CREATE TYPE sales.order_status AS ENUM ('draft', 'placed', 'paid', 'shipped', 'delivered', 'cancelled', 'refunded');

COMMENT ON TYPE sales.order_status IS $$
Where an order is in its life cycle. Allowed transitions are enforced by the order_header_status_guard trigger.
$$;
