CREATE VIEW order_totals AS
SELECT o.id AS order_id,
       c.name AS customer,
       o.status,
       sum(l.quantity * l.unit_price) AS total,
       platform.format_money(sum(l.quantity * l.unit_price), o.currency) AS total_display
FROM orders o
JOIN customers c ON c.id = o.customer_id
JOIN order_lines l ON l.order_id = o.id
GROUP BY o.id, c.name, o.status, o.currency;
COMMENT ON VIEW order_totals IS 'Each order''s total, with a display form in its currency.';
