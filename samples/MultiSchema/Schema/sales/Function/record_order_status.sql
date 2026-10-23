CREATE FUNCTION sales.record_order_status()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        INSERT INTO sales.order_status_history (order_id, from_status, to_status, changed_principal_id)
        VALUES (NEW.id, NULL, NEW.status, NEW.created_principal_id);
    ELSIF NEW.status IS DISTINCT FROM OLD.status THEN
        INSERT INTO sales.order_status_history (order_id, from_status, to_status, changed_principal_id)
        VALUES (NEW.id, OLD.status, NEW.status, NEW.updated_principal_id);
    END IF;

    RETURN NULL;
END;
$$;

COMMENT ON FUNCTION sales.record_order_status() IS $$
Trigger function for sales.order_header: writes a sales.order_status_history row when an order is created and each time its status changes, attributed to the principal that made the change.
$$;
