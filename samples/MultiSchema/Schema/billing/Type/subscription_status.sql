CREATE TYPE billing.subscription_status AS ENUM ('trialing', 'active', 'past_due', 'cancelled');

COMMENT ON TYPE billing.subscription_status IS $$
Where a subscription is in its life cycle.
$$;
