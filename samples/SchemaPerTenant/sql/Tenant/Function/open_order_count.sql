-- SET search_path FROM CURRENT keeps the shop's own schema first for this function, whoever calls it (see docs/notes/tenants.md).
CREATE FUNCTION open_order_count(customer bigint) RETURNS bigint
    LANGUAGE sql
    STABLE
    SET search_path FROM CURRENT
AS $$
    SELECT count(*) FROM orders WHERE customer_id = customer AND status IN ('placed', 'paid')
$$;
COMMENT ON FUNCTION open_order_count(bigint) IS 'Orders a customer has placed that have not shipped.';
