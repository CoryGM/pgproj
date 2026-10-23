CREATE TYPE billing.invoice_line_kind AS ENUM ('plan_fee', 'usage', 'proration', 'credit');

COMMENT ON TYPE billing.invoice_line_kind IS $$
What an invoice line charges for.
$$;
