CREATE TABLE sales.order_header (
    id                          uuid                            NOT NULL        DEFAULT public.uuid_generate_v7(),
    order_number                text                            NOT NULL        DEFAULT sales.next_order_number(),
    customer_id                 uuid                            NOT NULL,
    status                      sales.order_status              NOT NULL        DEFAULT 'draft',
    billing_address_id          uuid                            NULL,
    shipping_address_id         uuid                            NULL,
    discount_code_id            uuid                            NULL,
    subtotal                    public.money_amount             NOT NULL        DEFAULT 0,
    discount_total              public.money_amount             NOT NULL        DEFAULT 0,
    tax_total                   public.money_amount             NOT NULL        DEFAULT 0,
    shipping_total              public.money_amount             NOT NULL        DEFAULT 0,
    total                       numeric(12,2)                   GENERATED ALWAYS AS (subtotal - discount_total + tax_total + shipping_total) STORED,
    notes                       citext                          NULL,
    placed_utc                  timestamp without time zone     NULL,
    paid_utc                    timestamp without time zone     NULL,
    shipped_utc                 timestamp without time zone     NULL,
    delivered_utc               timestamp without time zone     NULL,
    cancelled_utc               timestamp without time zone     NULL,
    created_principal_id        uuid                            NOT NULL,
    created_utc                 timestamp without time zone     NOT NULL        DEFAULT public.utc_now(),
    updated_principal_id        uuid                            NOT NULL,
    updated_utc                 timestamp without time zone     NOT NULL        DEFAULT public.utc_now()
);

ALTER TABLE sales.order_header
    ADD CONSTRAINT order_header_pkey PRIMARY KEY (id);

ALTER TABLE sales.order_header
    ADD CONSTRAINT order_header_customer_id_fkey
        FOREIGN KEY (customer_id)
            REFERENCES sales.customer (id);

CREATE INDEX order_header_customer_id ON sales.order_header (customer_id);

ALTER TABLE sales.order_header
    ADD CONSTRAINT order_header_billing_address_id_fkey
        FOREIGN KEY (billing_address_id)
            REFERENCES sales.address (id);

CREATE INDEX order_header_billing_address_id ON sales.order_header (billing_address_id);

ALTER TABLE sales.order_header
    ADD CONSTRAINT order_header_shipping_address_id_fkey
        FOREIGN KEY (shipping_address_id)
            REFERENCES sales.address (id);

CREATE INDEX order_header_shipping_address_id ON sales.order_header (shipping_address_id);

ALTER TABLE sales.order_header
    ADD CONSTRAINT order_header_discount_code_id_fkey
        FOREIGN KEY (discount_code_id)
            REFERENCES sales.discount_code (id);

CREATE INDEX order_header_discount_code_id ON sales.order_header (discount_code_id);

ALTER TABLE sales.order_header
    ADD CONSTRAINT order_header_order_number_key UNIQUE (order_number);

ALTER TABLE sales.order_header
    ADD CONSTRAINT order_header_discount_within_subtotal CHECK (discount_total <= subtotal);

-- An order past draft must say where it is billed and shipped.
ALTER TABLE sales.order_header
    ADD CONSTRAINT order_header_addresses_after_draft CHECK (
        status IN ('draft', 'cancelled')
        OR (billing_address_id IS NOT NULL AND shipping_address_id IS NOT NULL)
    );

CREATE INDEX order_header_customer_placed ON sales.order_header (customer_id, placed_utc DESC);
CREATE INDEX order_header_open_status ON sales.order_header (status) WHERE status IN ('draft', 'placed', 'paid', 'shipped');
CREATE INDEX order_header_placed_utc ON sales.order_header (placed_utc) WHERE placed_utc IS NOT NULL;

CREATE TRIGGER order_header_status_guard
    BEFORE UPDATE OF status ON sales.order_header
    FOR EACH ROW
    WHEN (OLD.status IS DISTINCT FROM NEW.status)
    EXECUTE FUNCTION sales.guard_order_status();

CREATE TRIGGER order_header_status_history
    AFTER INSERT OR UPDATE OF status ON sales.order_header
    FOR EACH ROW
    EXECUTE FUNCTION sales.record_order_status();

ALTER TABLE sales.order_header
    ADD CONSTRAINT order_header_created_principal_id_fkey
        FOREIGN KEY (created_principal_id)
            REFERENCES identity.principal (id);

ALTER TABLE sales.order_header
    ADD CONSTRAINT order_header_updated_principal_id_fkey
        FOREIGN KEY (updated_principal_id)
            REFERENCES identity.principal (id);

CREATE INDEX order_header_created_principal_id ON sales.order_header (created_principal_id);
CREATE INDEX order_header_updated_principal_id ON sales.order_header (updated_principal_id);

CREATE TRIGGER order_header_set_updated_utc
    BEFORE UPDATE ON sales.order_header
    FOR EACH ROW
    EXECUTE FUNCTION public.set_updated_utc();

COMMENT ON COLUMN sales.order_header.created_principal_id IS $$
The principal that created the row.

@see-also: identity.principal.id
$$;

COMMENT ON COLUMN sales.order_header.created_utc IS $$
When the row was created, in UTC. Set by the database.

@example: 2026-10-09 14:03:00
$$;

COMMENT ON COLUMN sales.order_header.updated_principal_id IS $$
The principal that last changed the row. Equal to created_principal_id until the first change.

@see-also: identity.principal.id
$$;

COMMENT ON COLUMN sales.order_header.updated_utc IS $$
When the row was last changed, in UTC. Maintained by the set_updated_utc trigger.

@example: 2026-10-09 14:03:00
$$;

COMMENT ON TABLE sales.order_header IS $$
A customer order. Lines are in sales.order_line, payments in sales.payment, and every status change in sales.order_status_history. Totals are maintained by triggers from the lines; status moves through place_order, record_payment, and cancel_order.

@business-name: Order
$$;

COMMENT ON COLUMN sales.order_header.id IS $$
The surrogate key, a time-ordered UUID (version 7) generated by the database.
$$;

COMMENT ON COLUMN sales.order_header.order_number IS $$
The human-readable order number shown to the customer. Unique.

@see-also: sales.next_order_number
@example: SO-100042
$$;

COMMENT ON COLUMN sales.order_header.customer_id IS $$
The customer who placed the order.

@see-also: sales.customer.id
$$;

COMMENT ON COLUMN sales.order_header.status IS $$
Where the order is in its life cycle. Changes are validated by the order_header_status_guard trigger.
$$;

COMMENT ON COLUMN sales.order_header.billing_address_id IS $$
The address the order is billed to. Required once the order leaves draft.

@see-also: sales.address.id
$$;

COMMENT ON COLUMN sales.order_header.shipping_address_id IS $$
The address the order ships to. Required once the order leaves draft.

@see-also: sales.address.id
$$;

COMMENT ON COLUMN sales.order_header.discount_code_id IS $$
The promotion applied to the order, if any.

@see-also: sales.discount_code.id
$$;

COMMENT ON COLUMN sales.order_header.subtotal IS $$
Sum of the line totals before discount and tax. Maintained by the order_line_totals trigger.
$$;

COMMENT ON COLUMN sales.order_header.discount_total IS $$
Amount taken off the subtotal by the discount code. Maintained by the order_line_totals trigger.
$$;

COMMENT ON COLUMN sales.order_header.tax_total IS $$
Sales tax over all lines. Maintained by the order_line_totals trigger.
$$;

COMMENT ON COLUMN sales.order_header.shipping_total IS $$
Shipping charge. Free over the free-shipping threshold; see sales.shipping_charge.
$$;

COMMENT ON COLUMN sales.order_header.total IS $$
The amount the customer owes: subtotal - discount_total + tax_total + shipping_total. Generated.
$$;

COMMENT ON COLUMN sales.order_header.notes IS $$
Free-text notes from the customer or staff.
$$;

COMMENT ON COLUMN sales.order_header.placed_utc IS $$
When the order was placed, in UTC. Set by the status guard trigger.
$$;

COMMENT ON COLUMN sales.order_header.paid_utc IS $$
When the order was fully paid, in UTC. Set by the status guard trigger.
$$;

COMMENT ON COLUMN sales.order_header.shipped_utc IS $$
When the order shipped, in UTC. Set by the status guard trigger.
$$;

COMMENT ON COLUMN sales.order_header.delivered_utc IS $$
When the order was delivered, in UTC. Set by the status guard trigger.
$$;

COMMENT ON COLUMN sales.order_header.cancelled_utc IS $$
When the order was cancelled, in UTC. Set by the status guard trigger.
$$;
