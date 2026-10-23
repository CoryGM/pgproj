CREATE PROCEDURE sales.record_payment(
    p_order_id uuid,
    p_method sales.payment_method,
    p_amount numeric,
    p_external_reference text,
    p_principal_id uuid
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_order   sales.order_header%ROWTYPE;
    v_balance numeric;
BEGIN
    SELECT * INTO v_order FROM sales.order_header WHERE id = p_order_id FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Order % does not exist', p_order_id USING ERRCODE = 'no_data_found';
    END IF;
    IF v_order.status <> 'placed' THEN
        RAISE EXCEPTION 'Order % is % and is not awaiting payment', v_order.order_number, v_order.status
            USING ERRCODE = 'check_violation';
    END IF;

    v_balance := sales.order_balance(p_order_id);
    IF p_amount > v_balance THEN
        RAISE EXCEPTION 'Payment of % is more than the % still owed on order %', p_amount, v_balance, v_order.order_number
            USING ERRCODE = 'check_violation';
    END IF;

    INSERT INTO sales.payment
        (order_id, method, status, amount, external_reference, processed_utc, created_principal_id, updated_principal_id)
    VALUES
        (p_order_id, p_method, 'captured', p_amount, p_external_reference, public.utc_now(), p_principal_id, p_principal_id);

    -- Fully paid: move the order on.
    IF p_amount = v_balance THEN
        UPDATE sales.order_header
        SET status = 'paid',
            updated_principal_id = p_principal_id
        WHERE id = p_order_id;
    END IF;
END;
$$;

COMMENT ON PROCEDURE sales.record_payment(uuid, sales.payment_method, numeric, text, uuid) IS $$
Records a captured payment against a placed order and, when the payments cover the order total, moves the order to paid. Rejects a payment larger than the balance still owed.
$$;
