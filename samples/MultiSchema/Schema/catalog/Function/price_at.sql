CREATE FUNCTION catalog.price_at(p_product_id uuid, p_at timestamp without time zone DEFAULT public.utc_now())
RETURNS public.money_amount
LANGUAGE sql
STABLE
AS $$
    SELECT ph.unit_price
    FROM catalog.price_history AS ph
    WHERE ph.product_id = p_product_id
      AND ph.valid_from_utc <= p_at
      AND (ph.valid_to_utc IS NULL OR ph.valid_to_utc > p_at)
    ORDER BY ph.valid_from_utc DESC
    LIMIT 1;
$$;

COMMENT ON FUNCTION catalog.price_at(uuid, timestamp without time zone) IS $$
The price a product had at a point in time, from catalog.price_history. Returns null if the product had no price then. Defaults to now.

@example: select catalog.price_at(id, '2026-01-01') from catalog.product where sku = 'TENT-2P-GRN'
$$;
