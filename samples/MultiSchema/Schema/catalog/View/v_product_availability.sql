CREATE VIEW catalog.v_product_availability AS
SELECT p.id AS product_id,
       p.sku,
       p.name,
       catalog.category_path(p.category_id) AS category_path,
       p.unit_price,
       coalesce(s.quantity_on_hand, 0) AS quantity_on_hand,
       coalesce(s.warehouse_count, 0) AS warehouse_count,
       coalesce(s.quantity_on_hand, 0) > 0 AS is_in_stock,
       coalesce(s.quantity_on_hand, 0) <= coalesce(s.reorder_point, 0) AS needs_reorder
FROM catalog.product AS p
LEFT JOIN (
    SELECT sl.product_id,
           sum(sl.quantity_on_hand)::integer AS quantity_on_hand,
           sum(sl.reorder_point)::integer AS reorder_point,
           count(*)::integer AS warehouse_count
    FROM inventory.stock_level AS sl
    JOIN inventory.warehouse AS w ON w.id = sl.warehouse_id AND w.is_active
    GROUP BY sl.product_id
) AS s ON s.product_id = p.id
WHERE p.is_active;

COMMENT ON VIEW catalog.v_product_availability IS $$
Every active product with the stock it has across active warehouses: units on hand, how many warehouses hold it, whether any is in stock, and whether the total has fallen to the combined reorder points.
$$;
