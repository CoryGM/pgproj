CREATE FUNCTION inventory.apply_stock_movement()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    UPDATE inventory.stock_level
    SET quantity_on_hand     = quantity_on_hand + NEW.quantity_change,
        updated_principal_id = NEW.created_principal_id
    WHERE product_id = NEW.product_id
      AND warehouse_id = NEW.warehouse_id;

    -- First movement for this product and warehouse: create the level. (An INSERT ... ON CONFLICT would check the
    -- proposed row, and so reject a negative first movement, before it looked for the existing row.)
    IF NOT FOUND THEN
        INSERT INTO inventory.stock_level
            (product_id, warehouse_id, quantity_on_hand, created_principal_id, updated_principal_id)
        VALUES
            (NEW.product_id, NEW.warehouse_id, NEW.quantity_change, NEW.created_principal_id, NEW.created_principal_id);
    END IF;

    RETURN NULL;
END;
$$;

COMMENT ON FUNCTION inventory.apply_stock_movement() IS $$
Trigger function for inventory.stock_movement: applies each new movement to the stock_level row for its product and warehouse, creating the row on first use. A movement that would take stock below zero fails on the stock_level check constraint, which rolls the movement back.
$$;
