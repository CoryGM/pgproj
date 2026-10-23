CREATE FUNCTION sales.next_order_number()
RETURNS text
LANGUAGE sql
AS $$
    SELECT 'SO-' || nextval('sales.order_number_seq')::text;
$$;

COMMENT ON FUNCTION sales.next_order_number() IS $$
The next human-readable order number, such as SO-100042. The default for sales.order_header.order_number.

@see-also: sales.order_number_seq
$$;
