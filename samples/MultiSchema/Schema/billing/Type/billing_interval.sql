CREATE TYPE billing.billing_interval AS ENUM ('month', 'year');

COMMENT ON TYPE billing.billing_interval IS $$
How often a plan is billed.
$$;
