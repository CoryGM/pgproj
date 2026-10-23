CREATE TYPE sales.payment_method AS ENUM ('card', 'bank_transfer', 'wallet', 'gift_card');

COMMENT ON TYPE sales.payment_method IS $$
How a customer paid.
$$;
