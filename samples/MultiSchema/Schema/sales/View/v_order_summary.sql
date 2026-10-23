CREATE VIEW sales.v_order_summary AS
SELECT oh.id AS order_id,
       oh.order_number,
       oh.status,
       c.id AS customer_id,
       c.display_name AS customer_name,
       c.email AS customer_email,
       coalesce(l.line_count, 0) AS line_count,
       coalesce(l.unit_count, 0) AS unit_count,
       oh.subtotal,
       oh.discount_total,
       oh.tax_total,
       oh.shipping_total,
       oh.total,
       coalesce(p.paid_amount, 0) AS paid_amount,
       oh.total - coalesce(p.paid_amount, 0) AS balance_due,
       oh.placed_utc,
       oh.created_utc
FROM sales.order_header AS oh
JOIN sales.customer AS c ON c.id = oh.customer_id
LEFT JOIN (
    SELECT ol.order_id, count(*)::integer AS line_count, sum(ol.quantity)::integer AS unit_count
    FROM sales.order_line AS ol
    GROUP BY ol.order_id
) AS l ON l.order_id = oh.id
LEFT JOIN (
    SELECT pay.order_id, sum(pay.amount) AS paid_amount
    FROM sales.payment AS pay
    WHERE pay.status = 'captured'
    GROUP BY pay.order_id
) AS p ON p.order_id = oh.id;

COMMENT ON VIEW sales.v_order_summary IS $$
One row per order with its customer, line and unit counts, totals, captured payments, and the balance still due. The main query for order lists and customer service.
$$;
