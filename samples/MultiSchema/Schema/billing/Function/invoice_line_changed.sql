CREATE FUNCTION billing.invoice_line_changed()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    v_invoice_id uuid := CASE WHEN TG_OP = 'DELETE' THEN OLD.invoice_id ELSE NEW.invoice_id END;
BEGIN
    UPDATE billing.invoice AS i
    SET subtotal = coalesce((SELECT sum(il.amount) FROM billing.invoice_line AS il WHERE il.invoice_id = v_invoice_id), 0)
    WHERE i.id = v_invoice_id;

    RETURN NULL;
END;
$$;

COMMENT ON FUNCTION billing.invoice_line_changed() IS $$
Trigger function for billing.invoice_line: after a line is added, removed, or changed, sets the invoice subtotal to the sum of its line amounts.
$$;
