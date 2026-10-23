--  Plans
insert into billing.plan (id, code, name, description, billing_interval, price, included_units, overage_unit_price, trial_days, created_principal_id, updated_principal_id)
select md5('seed:plan:' || v.code)::uuid, v.code, v.name, v.description, v.billing_interval::billing.billing_interval,
       v.price, v.included_units, v.overage_unit_price, v.trial_days,
       '00000000-0000-0000-0000-000000000001'::uuid, '00000000-0000-0000-0000-000000000001'::uuid
from (values
    ('starter-monthly',    'Starter',    'For individuals getting going',        'month', 29.00,   1000,    0.0200, 0),
    ('pro-monthly',        'Pro',        'For small teams, billed monthly',      'month', 99.00,   10000,   0.0100, 14),
    ('pro-yearly',         'Pro Annual', 'Pro, billed yearly at a discount',     'year',  990.00,  120000,  0.0080, 14),
    ('enterprise-yearly',  'Enterprise', 'High volume with priority support',    'year',  4800.00, 1000000, 0.0040, 30)
) as v(code, name, description, billing_interval, price, included_units, overage_unit_price, trial_days)
on conflict (id) do nothing;

--  Subscriptions, dated relative to the day the sample is first deployed
insert into billing.subscription (id, account_id, plan_id, status, started_on, current_period_start, current_period_end, trial_end_on, cancel_at_period_end, cancelled_utc, created_principal_id, updated_principal_id)
select md5('seed:subscription:' || v.code)::uuid,
       md5('seed:account:' || v.account_code)::uuid,
       md5('seed:plan:' || v.plan_code)::uuid,
       v.status::billing.subscription_status,
       v.period_start - v.started_days_before_period,
       v.period_start,
       billing.period_end(v.period_start, p.billing_interval),
       v.trial_end_on, false,
       case when v.status = 'cancelled' then public.utc_now() - interval '60 days' end,
       '00000000-0000-0000-0000-000000000001'::uuid, '00000000-0000-0000-0000-000000000001'::uuid
from (values
    ('sam-starter',   'sam',   'starter-monthly', 'active',    current_date - 20,  70,  null::date),
    ('acme-pro',      'acme',  'pro-yearly',      'active',    current_date - 200, 0,   null),
    ('jane-trial',    'jane',  'pro-monthly',     'trialing',  current_date - 5,   0,   current_date + 9),
    ('priya-old',     'priya', 'starter-monthly', 'cancelled', current_date - 150, 30,  null)
) as v(code, account_code, plan_code, status, period_start, started_days_before_period, trial_end_on)
join billing.plan as p on p.id = md5('seed:plan:' || v.plan_code)::uuid
on conflict (id) do nothing;

--  Metered usage in the current period (the allowance on the Starter plan is 1000 units)
insert into billing.usage_event (subscription_id, quantity, occurred_utc, idempotency_key)
select md5('seed:subscription:' || v.subscription_code)::uuid, v.quantity,
       public.utc_now() - make_interval(days => v.days_ago), 'seed-' || v.subscription_code || '-' || v.days_ago
from (values
    ('sam-starter', 400, 18), ('sam-starter', 350, 12), ('sam-starter', 380, 5), ('sam-starter', 220, 1),
    ('acme-pro',   9000, 90), ('acme-pro',   15000, 60), ('acme-pro', 22000, 30), ('acme-pro', 18000, 7)
) as v(subscription_code, quantity, days_ago)
on conflict (subscription_id, idempotency_key) do nothing;

--  Past invoices, written directly: billing.run_billing_cycle commits as it goes, so it cannot be called from this
--  script, which runs inside the publish transaction.
do $seed$
declare
    c_system constant uuid := '00000000-0000-0000-0000-000000000002';
    v_sub    uuid := md5('seed:subscription:sam-starter')::uuid;
    v_acct   uuid := md5('seed:account:sam')::uuid;
    v_inv    uuid;
begin
    if exists (select 1 from billing.invoice where account_id in (md5('seed:account:sam')::uuid, md5('seed:account:acme')::uuid)) then
        return;
    end if;

    -- Two periods ago: still unpaid and overdue.
    v_inv := md5('seed:invoice:sam-2')::uuid;
    insert into billing.invoice (id, account_id, subscription_id, period_start, period_end, due_on, created_principal_id, updated_principal_id)
    values (v_inv, v_acct, v_sub, current_date - 80, current_date - 50, current_date - 36, c_system, c_system);
    insert into billing.invoice_line (invoice_id, line_number, kind, description, quantity, unit_price, created_principal_id, updated_principal_id)
    values (v_inv, 1, 'plan_fee', 'Starter plan', 1, 29.00, c_system, c_system),
           (v_inv, 2, 'usage', '125 units over the 1000 included', 125, 0.0200, c_system, c_system);
    update billing.invoice set status = 'open', issued_utc = public.utc_now() - interval '50 days' where id = v_inv;

    -- The last period: paid.
    v_inv := md5('seed:invoice:sam-1')::uuid;
    insert into billing.invoice (id, account_id, subscription_id, period_start, period_end, due_on, created_principal_id, updated_principal_id)
    values (v_inv, v_acct, v_sub, current_date - 50, current_date - 20, current_date - 6, c_system, c_system);
    insert into billing.invoice_line (invoice_id, line_number, kind, description, quantity, unit_price, created_principal_id, updated_principal_id)
    values (v_inv, 1, 'plan_fee', 'Starter plan', 1, 29.00, c_system, c_system);
    update billing.invoice set status = 'paid', issued_utc = public.utc_now() - interval '20 days', paid_utc = public.utc_now() - interval '15 days' where id = v_inv;

    -- A proration for Acme moving up a plan part way through its year: 200 of 365 days used.
    v_inv := md5('seed:invoice:acme-1')::uuid;
    insert into billing.invoice (id, account_id, subscription_id, period_start, period_end, due_on, created_principal_id, updated_principal_id)
    values (v_inv, md5('seed:account:acme')::uuid, null, current_date - 200, current_date, current_date + 7, c_system, c_system);
    insert into billing.invoice_line (invoice_id, line_number, kind, description, quantity, unit_price, created_principal_id, updated_principal_id)
    values (v_inv, 1, 'proration', 'Upgrade to Pro Annual, charged for the days remaining', 1,
            billing.prorate(990.00, current_date - 200, current_date + 165, current_date), c_system, c_system);
    update billing.invoice set status = 'open', issued_utc = public.utc_now() where id = v_inv;
end
$seed$;
