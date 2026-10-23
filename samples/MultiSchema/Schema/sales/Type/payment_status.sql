CREATE TYPE sales.payment_status AS ENUM ('pending', 'captured', 'failed', 'refunded');

COMMENT ON TYPE sales.payment_status IS $$
The state of a single payment attempt.
$$;
