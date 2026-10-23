CREATE TYPE sales.address_kind AS ENUM ('billing', 'shipping');

COMMENT ON TYPE sales.address_kind IS $$
What an address is used for.
$$;
