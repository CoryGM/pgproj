CREATE TYPE billing.invoice_status AS ENUM ('draft', 'open', 'paid', 'void');

COMMENT ON TYPE billing.invoice_status IS $$
Where an invoice is in its life cycle.
$$;
