CREATE TYPE inventory.movement_type AS ENUM ('receipt', 'sale', 'return', 'adjustment');

COMMENT ON TYPE inventory.movement_type IS $$
Why stock changed: a receipt from a supplier, a sale, a customer return, or a manual adjustment.
$$;
