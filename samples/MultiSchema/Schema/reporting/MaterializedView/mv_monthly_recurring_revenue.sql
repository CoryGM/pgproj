CREATE MATERIALIZED VIEW reporting.mv_monthly_recurring_revenue AS
SELECT pl.id AS plan_id,
       pl.code AS plan_code,
       pl.name AS plan_name,
       count(s.id)::integer AS subscription_count,
       coalesce(sum(
           CASE pl.billing_interval
               WHEN 'month' THEN pl.price
               WHEN 'year'  THEN round(pl.price / 12, 2)
           END
       ) FILTER (WHERE s.id IS NOT NULL), 0) AS mrr
FROM billing.plan AS pl
LEFT JOIN billing.subscription AS s
       ON s.plan_id = pl.id
      AND s.status IN ('active', 'past_due')
GROUP BY pl.id, pl.code, pl.name
WITH DATA;

CREATE UNIQUE INDEX mv_monthly_recurring_revenue_plan_id ON reporting.mv_monthly_recurring_revenue (plan_id);

COMMENT ON MATERIALIZED VIEW reporting.mv_monthly_recurring_revenue IS $$
Monthly recurring revenue per plan: the live (active or past-due) subscriptions on it and what they bring in each month, with yearly plans divided by twelve. Trials are not counted. Refresh with reporting.refresh_materialized_views.
$$;
