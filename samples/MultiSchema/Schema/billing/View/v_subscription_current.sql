CREATE VIEW billing.v_subscription_current AS
SELECT s.id AS subscription_id,
       s.account_id,
       a.name AS account_name,
       pl.code AS plan_code,
       pl.name AS plan_name,
       s.status,
       s.current_period_start,
       s.current_period_end,
       s.current_period_end - current_date AS days_until_renewal,
       s.cancel_at_period_end,
       CASE pl.billing_interval
           WHEN 'month' THEN pl.price
           WHEN 'year'  THEN round(pl.price / 12, 2)
       END AS monthly_equivalent_price,
       billing.usage_in_period(s.id, s.current_period_start, s.current_period_end) AS usage_this_period,
       pl.included_units
FROM billing.subscription AS s
JOIN billing.plan AS pl ON pl.id = s.plan_id
JOIN accounts.account AS a ON a.id = s.account_id
WHERE s.status IN ('trialing', 'active', 'past_due');

COMMENT ON VIEW billing.v_subscription_current IS $$
The live subscriptions (trialing, active, or past due) with the account and plan, the days until renewal, the plan price as a monthly figure, and the usage reported so far this period against the plan allowance.
$$;
