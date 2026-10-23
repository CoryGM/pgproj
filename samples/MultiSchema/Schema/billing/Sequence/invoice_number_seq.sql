CREATE SEQUENCE billing.invoice_number_seq
    START WITH 5001
    INCREMENT BY 1
    NO MAXVALUE
    CACHE 1;

COMMENT ON SEQUENCE billing.invoice_number_seq IS $$
Source of the numeric part of invoice numbers; see billing.next_invoice_number.
$$;
