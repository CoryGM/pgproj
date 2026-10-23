CREATE MATERIALIZED VIEW reporting.mv_customer_lifetime_value AS
SELECT c.id AS customer_id,
       c.display_name,
       c.account_id,
       count(oh.id)::integer AS order_count,
       coalesce(sum(oh.total), 0) AS lifetime_value,
       min(oh.placed_utc) AS first_order_utc,
       max(oh.placed_utc) AS last_order_utc
FROM sales.customer AS c
LEFT JOIN sales.order_header AS oh
       ON oh.customer_id = c.id
      AND oh.status IN ('placed', 'paid', 'shipped', 'delivered')
GROUP BY c.id, c.display_name, c.account_id
WITH DATA;

CREATE UNIQUE INDEX mv_customer_lifetime_value_customer_id ON reporting.mv_customer_lifetime_value (customer_id);
CREATE INDEX mv_customer_lifetime_value_value ON reporting.mv_customer_lifetime_value (lifetime_value DESC);

COMMENT ON MATERIALIZED VIEW reporting.mv_customer_lifetime_value IS $$
One row per customer with their order count, lifetime spend, and first and last order dates, counting orders that were placed and not cancelled or refunded. Customers who have not ordered have a count of zero. Refresh with reporting.refresh_materialized_views.
$$;
