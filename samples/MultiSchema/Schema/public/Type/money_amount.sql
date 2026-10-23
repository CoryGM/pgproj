CREATE DOMAIN public.money_amount AS numeric(12,2)
    CONSTRAINT money_amount_not_negative CHECK (VALUE >= 0);

-- A non-negative monetary amount with two decimal places. The currency is implied by the owning row or the
-- system default (USD). (pgproj does not model COMMENT ON DOMAIN, so the description is a plain comment.)
