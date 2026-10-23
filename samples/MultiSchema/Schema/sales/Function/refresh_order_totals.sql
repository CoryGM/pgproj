CREATE FUNCTION sales.refresh_order_totals(p_order_id uuid)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    v_subtotal numeric(12,2);
    v_tax      numeric(12,2);
    v_discount public.money_amount;
BEGIN
    SELECT coalesce(sum(ol.line_total), 0),
           coalesce(round(sum(ol.line_total * ol.tax_rate), 2), 0)
    INTO v_subtotal, v_tax
    FROM sales.order_line AS ol
    WHERE ol.order_id = p_order_id;

    SELECT sales.discount_amount(oh.discount_code_id, v_subtotal)
    INTO v_discount
    FROM sales.order_header AS oh
    WHERE oh.id = p_order_id;

    UPDATE sales.order_header
    SET subtotal       = v_subtotal,
        discount_total = v_discount,
        tax_total      = v_tax,
        shipping_total = sales.shipping_charge(v_subtotal - v_discount)
    WHERE id = p_order_id;
END;
$$;

COMMENT ON FUNCTION sales.refresh_order_totals(uuid) IS $$
Recomputes an order's subtotal, discount, tax, and shipping from its lines and discount code. Called by the order_line_totals trigger and by sales.apply_discount_code.
$$;
