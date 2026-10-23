CREATE PROCEDURE sales.cancel_order(p_order_id uuid, p_principal_id uuid, p_reason text DEFAULT NULL)
LANGUAGE plpgsql
AS $$
DECLARE
    v_order sales.order_header%ROWTYPE;
    v_sold  record;
BEGIN
    SELECT * INTO v_order FROM sales.order_header WHERE id = p_order_id FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Order % does not exist', p_order_id USING ERRCODE = 'no_data_found';
    END IF;

    IF v_order.status = 'cancelled' THEN
        RETURN;
    END IF;

    -- Put back the stock the order took. Summing per line and warehouse makes this safe to repeat.
    FOR v_sold IN
        SELECT sm.product_id, sm.warehouse_id, sm.order_line_id, -sum(sm.quantity_change) AS quantity
        FROM inventory.stock_movement AS sm
        JOIN sales.order_line AS ol ON ol.id = sm.order_line_id
        WHERE ol.order_id = p_order_id
        GROUP BY sm.product_id, sm.warehouse_id, sm.order_line_id
        HAVING sum(sm.quantity_change) < 0
    LOOP
        INSERT INTO inventory.stock_movement
            (product_id, warehouse_id, movement_type, quantity_change, order_line_id, reason, created_principal_id)
        VALUES
            (v_sold.product_id, v_sold.warehouse_id, 'return', v_sold.quantity, v_sold.order_line_id,
             coalesce(p_reason, 'Order ' || v_order.order_number || ' cancelled'), p_principal_id);
    END LOOP;

    UPDATE sales.payment
    SET status = 'refunded',
        updated_principal_id = p_principal_id
    WHERE order_id = p_order_id
      AND status = 'captured';

    IF v_order.status <> 'draft' AND v_order.discount_code_id IS NOT NULL THEN
        UPDATE sales.discount_code
        SET used_count = used_count - 1,
            updated_principal_id = p_principal_id
        WHERE id = v_order.discount_code_id;
    END IF;

    -- The status guard trigger rejects this if the order has already shipped.
    UPDATE sales.order_header
    SET status = 'cancelled',
        notes = coalesce(p_reason, notes),
        updated_principal_id = p_principal_id
    WHERE id = p_order_id;
END;
$$;

COMMENT ON PROCEDURE sales.cancel_order(uuid, uuid, text) IS $$
Cancels an order that has not shipped: returns its stock to the warehouses it came from, marks captured payments refunded, gives back the discount code use, and moves the order to cancelled. An order that has shipped fails on the status guard trigger and nothing is changed.
$$;
