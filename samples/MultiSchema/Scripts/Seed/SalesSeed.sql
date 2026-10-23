--  Accounts (the owner of customers, subscriptions and invoices)
--  An account's audit columns point at accounts, so each sample account is its own creator.
insert into accounts.account (id, name, description, created_principal_id, updated_principal_id)
select md5('seed:account:' || v.code)::uuid, v.name, v.description,
       md5('seed:account:' || v.code)::uuid, md5('seed:account:' || v.code)::uuid
from (values
    ('jane',  'Jane Doe',            'Individual shopper'),
    ('sam',   'Sam Rivera',          'Individual shopper with a starter subscription'),
    ('acme',  'Acme Outfitters Ltd', 'Guiding company buying gear in bulk'),
    ('priya', 'Priya Patel',         'Individual shopper')
) as v(code, name, description)
on conflict (id) do nothing;

--  Customers
insert into sales.customer (id, account_id, email, display_name, phone, marketing_opt_in, created_principal_id, updated_principal_id)
select md5('seed:customer:' || v.code)::uuid, md5('seed:account:' || v.code)::uuid,
       v.email, v.display_name, v.phone, v.marketing_opt_in,
       '00000000-0000-0000-0000-000000000001'::uuid, '00000000-0000-0000-0000-000000000001'::uuid
from (values
    ('jane',  'jane.doe@example.com',            'Jane Doe',            '+1 555 0100', true),
    ('sam',   'sam.rivera@example.com',          'Sam Rivera',          '+1 555 0101', false),
    ('acme',  'orders@acme-outfitters.example',  'Acme Outfitters Ltd', '+1 555 0102', true),
    ('priya', 'priya.patel@example.com',         'Priya Patel',         null,          false)
) as v(code, email, display_name, phone, marketing_opt_in)
on conflict (id) do nothing;

--  One default billing and one default shipping address each
insert into sales.address (id, customer_id, kind, recipient, line1, line2, city, region, postal_code, country_code, is_default, created_principal_id, updated_principal_id)
select md5('seed:address:' || v.code || ':' || k.kind)::uuid, md5('seed:customer:' || v.code)::uuid, k.kind::sales.address_kind,
       v.recipient, v.line1, v.line2, v.city, v.region, v.postal_code, 'US', true,
       '00000000-0000-0000-0000-000000000001'::uuid, '00000000-0000-0000-0000-000000000001'::uuid
from (values
    ('jane',  'Jane Doe',            '14 Elm Street',        null,      'Austin',  'TX', '78701'),
    ('sam',   'Sam Rivera',          '902 Harbor Way',       'Apt 4B',  'Seattle', 'WA', '98101'),
    ('acme',  'Acme Outfitters Ltd', '2200 Industrial Blvd', 'Dock 3',  'Denver',  'CO', '80216'),
    ('priya', 'Priya Patel',         '77 Lakeview Drive',    null,      'Chicago', 'IL', '60601')
) as v(code, recipient, line1, line2, city, region, postal_code)
cross join (values ('billing'), ('shipping')) as k(kind)
on conflict (id) do nothing;

--  Promotion codes
insert into sales.discount_code (id, code, description, percent_off, amount_off, minimum_subtotal, max_uses, created_principal_id, updated_principal_id)
select md5('seed:discount:' || v.code)::uuid, v.code, v.description, v.percent_off, v.amount_off, v.minimum_subtotal, v.max_uses,
       '00000000-0000-0000-0000-000000000001'::uuid, '00000000-0000-0000-0000-000000000001'::uuid
from (values
    ('WELCOME10', 'Ten percent off a first order',            10.00::numeric, null::numeric, 0::numeric,   null::integer),
    ('TRAIL25',   'Twenty-five dollars off orders of 200+',   null,           25.00,         200.00,       500)
) as v(code, description, percent_off, amount_off, minimum_subtotal, max_uses)
on conflict (id) do nothing;

--  Sample orders, taken through the real procedures so the triggers, stock ledger and history are exercised.
--  Each order is created once: an order whose id already exists is skipped.
do $seed$
declare
    c_system constant uuid := '00000000-0000-0000-0000-000000000002';
    v_order  jsonb;
    v_line   jsonb;
    v_id     uuid;
    v_n      integer;
    v_days   integer;
    v_flow   text;
begin
    for v_order in select * from jsonb_array_elements($json$[
        {"key": "o01", "customer": "jane",  "days": 45, "flow": "delivered", "lines": [["TENT-2P-GRN", 1], ["PAD-INS", 2], ["SOCK-WOOL", 4]]},
        {"key": "o02", "customer": "jane",  "days": 12, "flow": "paid",      "code": "TRAIL25",   "lines": [["JKT-RAIN", 1], ["BOOT-HIKE", 1]]},
        {"key": "o03", "customer": "sam",   "days": 30, "flow": "delivered", "code": "WELCOME10", "lines": [["BAG-20F", 1], ["STOVE-1", 1], ["POT-TI", 1]]},
        {"key": "o04", "customer": "sam",   "days": 6,  "flow": "shipped",   "lines": [["HEAD-LAMP", 2], ["LANT-LED", 1]]},
        {"key": "o05", "customer": "acme",  "days": 21, "flow": "delivered", "lines": [["TENT-4P-ORG", 3], ["BAG-20F", 6], ["KIT-MESS", 6]]},
        {"key": "o06", "customer": "acme",  "days": 3,  "flow": "paid",      "lines": [["JKT-DOWN", 5], ["FILTER-WTR", 3]]},
        {"key": "o07", "customer": "priya", "days": 9,  "flow": "placed",    "lines": [["BAG-0F", 1], ["PAD-INS", 1]]},
        {"key": "o08", "customer": "priya", "days": 18, "flow": "cancelled", "lines": [["TENT-2P-GRN", 1]]},
        {"key": "o09", "customer": "jane",  "days": 0,  "flow": "draft",     "lines": [["SOCK-WOOL", 2]]},
        {"key": "o10", "customer": "acme",  "days": 1,  "flow": "placed",    "lines": [["FILTER-WTR", 2]]}
    ]$json$::jsonb)
    loop
        v_id   := md5('seed:order:' || (v_order->>'key'))::uuid;
        v_days := (v_order->>'days')::integer;
        v_flow := v_order->>'flow';

        continue when exists (select 1 from sales.order_header where id = v_id);

        insert into sales.order_header (id, customer_id, billing_address_id, shipping_address_id, created_principal_id, updated_principal_id)
        values (v_id,
                md5('seed:customer:' || (v_order->>'customer'))::uuid,
                md5('seed:address:' || (v_order->>'customer') || ':billing')::uuid,
                md5('seed:address:' || (v_order->>'customer') || ':shipping')::uuid,
                c_system, c_system);

        v_n := 0;
        for v_line in select * from jsonb_array_elements(v_order->'lines') loop
            v_n := v_n + 1;
            insert into sales.order_line (order_id, line_number, product_id, description, quantity, unit_price, tax_rate, created_principal_id, updated_principal_id)
            select v_id, v_n, p.id, p.name, (v_line->>1)::integer, p.unit_price, p.tax_rate, c_system, c_system
            from catalog.product as p
            where p.sku = v_line->>0;
        end loop;

        if v_order ? 'code' then
            call sales.apply_discount_code(v_id, v_order->>'code', c_system);
        end if;
        if v_flow <> 'draft' then
            call sales.place_order(v_id, c_system);
        end if;
        if v_flow in ('paid', 'shipped', 'delivered', 'cancelled') then
            call sales.record_payment(v_id, 'card', sales.order_balance(v_id), 'seed-' || (v_order->>'key'), c_system);
        end if;
        if v_flow in ('shipped', 'delivered') then
            update sales.order_header set status = 'shipped', updated_principal_id = c_system where id = v_id;
        end if;
        if v_flow = 'delivered' then
            update sales.order_header set status = 'delivered', updated_principal_id = c_system where id = v_id;
        end if;
        if v_flow = 'cancelled' then
            call sales.cancel_order(v_id, c_system, 'Customer changed their mind');
        end if;

        -- Backdate the order so the reports have several weeks of history.
        update sales.order_header
        set placed_utc    = placed_utc    - make_interval(days => v_days),
            paid_utc      = paid_utc      - make_interval(days => v_days),
            shipped_utc   = shipped_utc   - make_interval(days => v_days),
            delivered_utc = delivered_utc - make_interval(days => v_days),
            cancelled_utc = cancelled_utc - make_interval(days => v_days)
        where id = v_id;
    end loop;
end
$seed$;
