CREATE MATERIALIZED VIEW reporting.mv_low_stock AS
SELECT p.id AS product_id,
       p.sku,
       p.name,
       sum(sl.quantity_on_hand)::integer AS quantity_on_hand,
       sum(sl.reorder_point)::integer AS reorder_point,
       (sum(sl.reorder_point) - sum(sl.quantity_on_hand))::integer AS shortfall
FROM inventory.stock_level AS sl
JOIN inventory.warehouse AS w ON w.id = sl.warehouse_id AND w.is_active
JOIN catalog.product AS p ON p.id = sl.product_id AND p.is_active
GROUP BY p.id, p.sku, p.name
HAVING sum(sl.quantity_on_hand) <= sum(sl.reorder_point)
WITH DATA;

CREATE UNIQUE INDEX mv_low_stock_product_id ON reporting.mv_low_stock (product_id);

COMMENT ON MATERIALIZED VIEW reporting.mv_low_stock IS $$
Active products whose stock across active warehouses has fallen to or below their combined reorder points, with how far short they are. The purchasing worklist. Refresh with reporting.refresh_materialized_views.
$$;
