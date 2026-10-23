CREATE PROCEDURE sales.apply_discount_code(p_order_id uuid, p_code text, p_principal_id uuid)
LANGUAGE plpgsql
AS $$
DECLARE
    v_status      sales.order_status;
    v_code_id     uuid;
    v_subtotal    numeric;
BEGIN
    SELECT status, subtotal INTO v_status, v_subtotal FROM sales.order_header WHERE id = p_order_id FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Order % does not exist', p_order_id USING ERRCODE = 'no_data_found';
    END IF;
    IF v_status <> 'draft' THEN
        RAISE EXCEPTION 'A discount code can only be applied to a draft order (order is %)', v_status
            USING ERRCODE = 'check_violation';
    END IF;

    SELECT dc.id INTO v_code_id
    FROM sales.discount_code AS dc
    WHERE dc.code = p_code
      AND (dc.max_uses IS NULL OR dc.used_count < dc.max_uses);

    IF v_code_id IS NULL OR sales.discount_amount(v_code_id, v_subtotal) = 0 THEN
        RAISE EXCEPTION 'Discount code "%" cannot be applied to this order', p_code USING ERRCODE = 'check_violation';
    END IF;

    UPDATE sales.order_header
    SET discount_code_id = v_code_id,
        updated_principal_id = p_principal_id
    WHERE id = p_order_id;

    PERFORM sales.refresh_order_totals(p_order_id);
END;
$$;

COMMENT ON PROCEDURE sales.apply_discount_code(uuid, text, uuid) IS $$
Applies a discount code to a draft order and recomputes its totals. Fails if the code is unknown, inactive, expired, used up, or does not reduce this order (for example the subtotal is below its minimum).
$$;
