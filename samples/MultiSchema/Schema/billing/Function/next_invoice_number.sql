CREATE FUNCTION billing.next_invoice_number()
RETURNS text
LANGUAGE sql
AS $$
    SELECT 'INV-' || lpad(nextval('billing.invoice_number_seq')::text, 6, '0');
$$;

COMMENT ON FUNCTION billing.next_invoice_number() IS $$
The next invoice number, such as INV-005001. The default for billing.invoice.invoice_number.

@see-also: billing.invoice_number_seq
$$;
