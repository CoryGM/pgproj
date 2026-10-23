CREATE MATERIALIZED VIEW reporting.mv_daily_sales AS
SELECT oh.placed_utc::date AS sale_date,
       count(*)::integer AS order_count,
       sum(u.unit_count)::integer AS units_sold,
       sum(oh.subtotal) AS gross_sales,
       sum(oh.discount_total) AS discounts,
       sum(oh.total) AS net_revenue,
       round(avg(oh.total), 2) AS average_order_value
FROM sales.order_header AS oh
JOIN (
    SELECT ol.order_id, sum(ol.quantity) AS unit_count
    FROM sales.order_line AS ol
    GROUP BY ol.order_id
) AS u ON u.order_id = oh.id
WHERE oh.status IN ('placed', 'paid', 'shipped', 'delivered')
GROUP BY oh.placed_utc::date
WITH DATA;

CREATE UNIQUE INDEX mv_daily_sales_sale_date ON reporting.mv_daily_sales (sale_date);

COMMENT ON MATERIALIZED VIEW reporting.mv_daily_sales IS $$
Sales per day for orders that were placed and not cancelled or refunded: order count, units, gross sales, discounts, net revenue, and average order value. Refresh with reporting.refresh_materialized_views.
$$;
