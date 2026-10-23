CREATE FUNCTION sales.is_valid_order_transition(p_from sales.order_status, p_to sales.order_status)
RETURNS boolean
LANGUAGE sql
IMMUTABLE
AS $$
    SELECT CASE p_from
        WHEN 'draft'     THEN p_to IN ('placed', 'cancelled')
        WHEN 'placed'    THEN p_to IN ('paid', 'cancelled')
        WHEN 'paid'      THEN p_to IN ('shipped', 'cancelled')
        WHEN 'shipped'   THEN p_to IN ('delivered', 'refunded')
        WHEN 'delivered' THEN p_to IN ('refunded')
        ELSE false
    END;
$$;

COMMENT ON FUNCTION sales.is_valid_order_transition(sales.order_status, sales.order_status) IS $$
Whether an order may move from one status to another. Cancelled and refunded are final.

draft -> placed -> paid -> shipped -> delivered, with cancel allowed until the order ships and refund allowed after.
$$;
