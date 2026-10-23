CREATE FUNCTION sales.order_line_changed()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    PERFORM sales.refresh_order_totals(CASE WHEN TG_OP = 'DELETE' THEN OLD.order_id ELSE NEW.order_id END);
    RETURN NULL;
END;
$$;

COMMENT ON FUNCTION sales.order_line_changed() IS $$
Trigger function for sales.order_line: after a line is added, removed, or repriced, refreshes the totals on its order.
$$;
