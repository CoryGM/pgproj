CREATE PROCEDURE billing.run_billing_cycle(p_as_of date, p_principal_id uuid)
LANGUAGE plpgsql
AS $$
DECLARE
    v_sub          record;
    v_invoice_id   uuid;
    v_line_id      uuid;
    v_usage        bigint;
    v_overage      bigint;
    v_next_start   date;
BEGIN
    FOR v_sub IN
        SELECT s.id, s.account_id, s.status, s.current_period_start, s.current_period_end,
               s.trial_end_on, s.cancel_at_period_end,
               p.name AS plan_name, p.price, p.billing_interval, p.included_units, p.overage_unit_price
        FROM billing.subscription AS s
        JOIN billing.plan AS p ON p.id = s.plan_id
        WHERE s.status IN ('trialing', 'active', 'past_due')
          AND s.current_period_end <= p_as_of
        ORDER BY s.current_period_end, s.id
    LOOP
        -- Still in the free trial: nothing to bill, just roll the period on.
        IF v_sub.status = 'trialing' AND v_sub.trial_end_on > v_sub.current_period_end THEN
            UPDATE billing.subscription
            SET current_period_start = v_sub.current_period_end,
                current_period_end   = billing.period_end(v_sub.current_period_end, v_sub.billing_interval),
                updated_principal_id = p_principal_id
            WHERE id = v_sub.id;
            CONTINUE;
        END IF;

        -- Invoice the period that just ended (billing in arrears).
        INSERT INTO billing.invoice
            (account_id, subscription_id, period_start, period_end, due_on, created_principal_id, updated_principal_id)
        VALUES
            (v_sub.account_id, v_sub.id, v_sub.current_period_start, v_sub.current_period_end,
             p_as_of + 14, p_principal_id, p_principal_id)
        RETURNING id INTO v_invoice_id;

        INSERT INTO billing.invoice_line
            (invoice_id, line_number, kind, description, quantity, unit_price, created_principal_id, updated_principal_id)
        VALUES
            (v_invoice_id, 1, 'plan_fee',
             v_sub.plan_name || ' plan, ' || v_sub.current_period_start || ' to ' || v_sub.current_period_end,
             1, v_sub.price, p_principal_id, p_principal_id);

        -- Metered usage beyond the plan allowance.
        SELECT coalesce(sum(ue.quantity), 0) INTO v_usage
        FROM billing.usage_event AS ue
        WHERE ue.subscription_id = v_sub.id
          AND ue.invoice_line_id IS NULL
          AND ue.occurred_utc >= v_sub.current_period_start
          AND ue.occurred_utc <  v_sub.current_period_end;

        v_overage := greatest(v_usage - v_sub.included_units, 0);

        IF v_overage > 0 THEN
            INSERT INTO billing.invoice_line
                (invoice_id, line_number, kind, description, quantity, unit_price, created_principal_id, updated_principal_id)
            VALUES
                (v_invoice_id, 2, 'usage', v_overage || ' units over the ' || v_sub.included_units || ' included',
                 v_overage, v_sub.overage_unit_price, p_principal_id, p_principal_id)
            RETURNING id INTO v_line_id;
        END IF;

        -- Stamp the billed events, including those inside the allowance, so none is billed twice.
        UPDATE billing.usage_event AS ue
        SET invoice_line_id = coalesce(v_line_id, (SELECT il.id FROM billing.invoice_line AS il
                                                   WHERE il.invoice_id = v_invoice_id AND il.line_number = 1))
        WHERE ue.subscription_id = v_sub.id
          AND ue.invoice_line_id IS NULL
          AND ue.occurred_utc >= v_sub.current_period_start
          AND ue.occurred_utc <  v_sub.current_period_end;
        v_line_id := NULL;

        UPDATE billing.invoice
        SET status = 'open',
            issued_utc = public.utc_now(),
            updated_principal_id = p_principal_id
        WHERE id = v_invoice_id;

        -- Renew, or end the subscription if it was set to cancel.
        IF v_sub.cancel_at_period_end THEN
            UPDATE billing.subscription
            SET status = 'cancelled',
                cancelled_utc = public.utc_now(),
                updated_principal_id = p_principal_id
            WHERE id = v_sub.id;
        ELSE
            v_next_start := v_sub.current_period_end;
            UPDATE billing.subscription
            SET status = 'active',
                current_period_start = v_next_start,
                current_period_end   = billing.period_end(v_next_start, v_sub.billing_interval),
                updated_principal_id = p_principal_id
            WHERE id = v_sub.id;
        END IF;

        -- Each subscription is its own unit of work, so one failure does not undo the others.
        COMMIT;
    END LOOP;
END;
$$;

COMMENT ON PROCEDURE billing.run_billing_cycle(date, uuid) IS $$
Bills every live subscription whose current period has ended by the given date. For each one it raises an open invoice with the plan fee and any usage beyond the plan allowance, stamps the usage events it billed, and then renews the subscription for the next period, or cancels it if it was set to cancel at period end. Subscriptions still in a free trial are rolled forward without an invoice.

Commits after each subscription, so it must be called outside a transaction block.

@example: call billing.run_billing_cycle(current_date, '00000000-0000-0000-0000-000000000002')
$$;
