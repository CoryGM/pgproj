CREATE FUNCTION inventory.available_quantity(p_product_id uuid)
RETURNS integer
LANGUAGE sql
STABLE
AS $$
    SELECT coalesce(sum(sl.quantity_on_hand), 0)::integer
    FROM inventory.stock_level AS sl
    JOIN inventory.warehouse AS w ON w.id = sl.warehouse_id
    WHERE sl.product_id = p_product_id
      AND w.is_active;
$$;

COMMENT ON FUNCTION inventory.available_quantity(uuid) IS $$
Units of a product on hand across all active warehouses. Zero if the product has no stock.
$$;
