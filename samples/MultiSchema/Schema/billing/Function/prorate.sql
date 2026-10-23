CREATE FUNCTION billing.prorate(p_amount numeric, p_period_start date, p_period_end date, p_change_on date)
RETURNS numeric
LANGUAGE sql
IMMUTABLE
AS $$
    -- The share of the period from the change date to the end, clamped to the period.
    SELECT round(
        p_amount
        * greatest(0, least(p_period_end - p_change_on, p_period_end - p_period_start))
        / nullif(p_period_end - p_period_start, 0),
        2
    );
$$;

COMMENT ON FUNCTION billing.prorate(numeric, date, date, date) IS $$
The part of a period charge that applies from a change date to the end of the period, by days. Used to credit the unused part of the old plan and charge the new one when a subscription changes mid-period. A change date outside the period is clamped to it.

@example: select billing.prorate(30.00, '2026-06-01', '2026-07-01', '2026-06-16')  -- 15.00
$$;
