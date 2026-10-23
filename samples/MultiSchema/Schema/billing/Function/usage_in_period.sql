CREATE FUNCTION billing.usage_in_period(p_subscription_id uuid, p_period_start date, p_period_end date)
RETURNS bigint
LANGUAGE sql
STABLE
AS $$
    SELECT coalesce(sum(ue.quantity), 0)
    FROM billing.usage_event AS ue
    WHERE ue.subscription_id = p_subscription_id
      AND ue.occurred_utc >= p_period_start
      AND ue.occurred_utc <  p_period_end;
$$;

COMMENT ON FUNCTION billing.usage_in_period(uuid, date, date) IS $$
Total usage units reported for a subscription from the start date (inclusive) to the end date (exclusive), whether or not they have been billed.
$$;
