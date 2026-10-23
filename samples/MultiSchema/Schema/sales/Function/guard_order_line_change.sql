CREATE FUNCTION sales.guard_order_line_change()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    v_order_id uuid := CASE WHEN TG_OP = 'DELETE' THEN OLD.order_id ELSE NEW.order_id END;
    v_status   sales.order_status;
BEGIN
    SELECT status INTO v_status FROM sales.order_header WHERE id = v_order_id;

    IF v_status IS DISTINCT FROM 'draft' THEN
        RAISE EXCEPTION 'Lines can only be changed while the order is a draft (order is %)', v_status
            USING ERRCODE = 'check_violation';
    END IF;

    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END;
$$;

COMMENT ON FUNCTION sales.guard_order_line_change() IS $$
Trigger function for sales.order_line: rejects adding, removing, or changing the product, quantity, or price of a line once its order has left draft.
$$;
