CREATE FUNCTION sales.guard_order_status()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF NOT sales.is_valid_order_transition(OLD.status, NEW.status) THEN
        RAISE EXCEPTION 'Order % cannot move from % to %', OLD.order_number, OLD.status, NEW.status
            USING ERRCODE = 'check_violation';
    END IF;

    CASE NEW.status
        WHEN 'placed'    THEN NEW.placed_utc    := public.utc_now();
        WHEN 'paid'      THEN NEW.paid_utc      := public.utc_now();
        WHEN 'shipped'   THEN NEW.shipped_utc   := public.utc_now();
        WHEN 'delivered' THEN NEW.delivered_utc := public.utc_now();
        WHEN 'cancelled' THEN NEW.cancelled_utc := public.utc_now();
        ELSE NULL;
    END CASE;

    RETURN NEW;
END;
$$;

COMMENT ON FUNCTION sales.guard_order_status() IS $$
Trigger function for sales.order_header: rejects a status change that is not allowed by sales.is_valid_order_transition and stamps the timestamp that goes with the new status.
$$;
