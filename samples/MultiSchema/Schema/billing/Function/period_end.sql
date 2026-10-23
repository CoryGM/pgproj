CREATE FUNCTION billing.period_end(p_period_start date, p_interval billing.billing_interval)
RETURNS date
LANGUAGE sql
IMMUTABLE
AS $$
    SELECT (p_period_start + CASE p_interval
        WHEN 'month' THEN interval '1 month'
        WHEN 'year'  THEN interval '1 year'
    END)::date;
$$;

COMMENT ON FUNCTION billing.period_end(date, billing.billing_interval) IS $$
The exclusive end of a billing period that starts on the given date: one month or one year later.

@example: select billing.period_end('2026-01-31', 'month')  -- 2026-02-28
$$;
