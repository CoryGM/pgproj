CREATE SEQUENCE sales.order_number_seq
    START WITH 100001
    INCREMENT BY 1
    NO MAXVALUE
    CACHE 1;

COMMENT ON SEQUENCE sales.order_number_seq IS $$
Source of the numeric part of human-readable order numbers; see sales.next_order_number.
$$;
