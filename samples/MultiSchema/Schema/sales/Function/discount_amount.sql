CREATE FUNCTION sales.discount_amount(
    p_discount_code_id uuid,
    p_subtotal numeric,
    p_at timestamp without time zone DEFAULT public.utc_now()
)
RETURNS public.money_amount
LANGUAGE sql
STABLE
AS $$
    SELECT coalesce(
        (
            SELECT least(
                p_subtotal,
                CASE
                    WHEN dc.percent_off IS NOT NULL THEN round(p_subtotal * dc.percent_off / 100, 2)
                    ELSE dc.amount_off
                END
            )
            FROM sales.discount_code AS dc
            WHERE dc.id = p_discount_code_id
              AND dc.is_active
              AND dc.valid_from_utc <= p_at
              AND (dc.valid_to_utc IS NULL OR dc.valid_to_utc > p_at)
              AND p_subtotal >= dc.minimum_subtotal
        ),
        0
    )::public.money_amount;
$$;

COMMENT ON FUNCTION sales.discount_amount(uuid, numeric, timestamp without time zone) IS $$
How much a discount code takes off a subtotal at a point in time: the percentage or fixed amount, never more than the subtotal. Zero when the code is unknown, inactive, outside its validity window, or the subtotal is below its minimum.
$$;
