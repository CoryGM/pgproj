CREATE FUNCTION sales.shipping_charge(p_subtotal numeric)
RETURNS public.money_amount
LANGUAGE sql
IMMUTABLE
AS $$
    -- Flat rate, free on orders of 100.00 or more, nothing on an empty order.
    SELECT CASE
        WHEN p_subtotal <= 0   THEN 0
        WHEN p_subtotal >= 100 THEN 0
        ELSE 7.50
    END::public.money_amount;
$$;

COMMENT ON FUNCTION sales.shipping_charge(numeric) IS $$
The shipping charge for an order subtotal: a flat 7.50, free from 100.00, and nothing for an empty order.
$$;
