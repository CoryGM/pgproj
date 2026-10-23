CREATE FUNCTION sales.order_balance(p_order_id uuid)
RETURNS numeric
LANGUAGE sql
STABLE
AS $$
    SELECT oh.total - coalesce(
        (SELECT sum(p.amount) FROM sales.payment AS p WHERE p.order_id = oh.id AND p.status = 'captured'),
        0
    )
    FROM sales.order_header AS oh
    WHERE oh.id = p_order_id;
$$;

COMMENT ON FUNCTION sales.order_balance(uuid) IS $$
What a customer still owes on an order: the order total less its captured payments. Null for an unknown order.
$$;
