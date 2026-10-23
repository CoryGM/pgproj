CREATE PROCEDURE sales.place_order(p_order_id uuid, p_principal_id uuid)
LANGUAGE plpgsql
AS $$
DECLARE
    v_order     sales.order_header%ROWTYPE;
    v_line      sales.order_line%ROWTYPE;
    v_stock     record;
    v_remaining integer;
    v_take      integer;
BEGIN
    SELECT * INTO v_order FROM sales.order_header WHERE id = p_order_id FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Order % does not exist', p_order_id USING ERRCODE = 'no_data_found';
    END IF;
    IF v_order.status <> 'draft' THEN
        RAISE EXCEPTION 'Order % is % and can no longer be placed', v_order.order_number, v_order.status
            USING ERRCODE = 'check_violation';
    END IF;
    IF v_order.billing_address_id IS NULL OR v_order.shipping_address_id IS NULL THEN
        RAISE EXCEPTION 'Order % needs a billing and a shipping address before it can be placed', v_order.order_number
            USING ERRCODE = 'check_violation';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM sales.order_line WHERE order_id = p_order_id) THEN
        RAISE EXCEPTION 'Order % has no lines', v_order.order_number USING ERRCODE = 'check_violation';
    END IF;

    -- Reserve stock line by line, taking from the warehouses holding the most first.
    FOR v_line IN SELECT * FROM sales.order_line WHERE order_id = p_order_id ORDER BY line_number LOOP
        v_remaining := v_line.quantity;

        FOR v_stock IN
            SELECT sl.warehouse_id, sl.quantity_on_hand
            FROM inventory.stock_level AS sl
            JOIN inventory.warehouse AS w ON w.id = sl.warehouse_id AND w.is_active
            WHERE sl.product_id = v_line.product_id
              AND sl.quantity_on_hand > 0
            ORDER BY sl.quantity_on_hand DESC
            FOR UPDATE OF sl
        LOOP
            EXIT WHEN v_remaining = 0;
            v_take := least(v_remaining, v_stock.quantity_on_hand);

            INSERT INTO inventory.stock_movement
                (product_id, warehouse_id, movement_type, quantity_change, order_line_id, created_principal_id)
            VALUES
                (v_line.product_id, v_stock.warehouse_id, 'sale', -v_take, v_line.id, p_principal_id);

            v_remaining := v_remaining - v_take;
        END LOOP;

        IF v_remaining > 0 THEN
            RAISE EXCEPTION 'Not enough stock of "%" for order %: % short', v_line.description, v_order.order_number, v_remaining
                USING ERRCODE = 'check_violation';
        END IF;
    END LOOP;

    IF v_order.discount_code_id IS NOT NULL THEN
        UPDATE sales.discount_code
        SET used_count = used_count + 1,
            updated_principal_id = p_principal_id
        WHERE id = v_order.discount_code_id;
    END IF;

    UPDATE sales.order_header
    SET status = 'placed',
        updated_principal_id = p_principal_id
    WHERE id = p_order_id;
END;
$$;

COMMENT ON PROCEDURE sales.place_order(uuid, uuid) IS $$
Places a draft order: takes the stock for every line out of the warehouses (one inventory.stock_movement per warehouse used), counts the discount code use, and moves the order to placed. All or nothing: if any line cannot be filled, nothing is changed.

@example: call sales.place_order('018f...', '00000000-0000-0000-0000-000000000002')
$$;
