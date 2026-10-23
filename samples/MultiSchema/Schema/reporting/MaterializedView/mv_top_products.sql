CREATE MATERIALIZED VIEW reporting.mv_top_products AS
SELECT ranked.product_id,
       ranked.sku,
       ranked.name,
       ranked.units_sold,
       ranked.revenue,
       rank() OVER (ORDER BY ranked.revenue DESC) AS revenue_rank
FROM (
    SELECT p.id AS product_id,
           p.sku,
           p.name,
           sum(ol.quantity)::integer AS units_sold,
           sum(ol.line_total) AS revenue
    FROM sales.order_line AS ol
    JOIN sales.order_header AS oh ON oh.id = ol.order_id
    JOIN catalog.product AS p ON p.id = ol.product_id
    WHERE oh.status IN ('placed', 'paid', 'shipped', 'delivered')
    GROUP BY p.id, p.sku, p.name
) AS ranked
WITH DATA;

CREATE UNIQUE INDEX mv_top_products_product_id ON reporting.mv_top_products (product_id);
CREATE INDEX mv_top_products_revenue_rank ON reporting.mv_top_products (revenue_rank);

COMMENT ON MATERIALIZED VIEW reporting.mv_top_products IS $$
Products ranked by revenue across all orders that were placed and not cancelled or refunded, with units sold. Refresh with reporting.refresh_materialized_views.
$$;
