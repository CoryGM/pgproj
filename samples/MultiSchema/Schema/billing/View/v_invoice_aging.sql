CREATE VIEW billing.v_invoice_aging AS
SELECT i.id AS invoice_id,
       i.invoice_number,
       i.account_id,
       a.name AS account_name,
       i.total,
       i.due_on,
       greatest(current_date - i.due_on, 0) AS days_overdue,
       CASE
           WHEN i.due_on >= current_date THEN 'current'
           WHEN current_date - i.due_on <= 30 THEN '1-30'
           WHEN current_date - i.due_on <= 60 THEN '31-60'
           ELSE '61+'
       END AS aging_bucket
FROM billing.invoice AS i
JOIN accounts.account AS a ON a.id = i.account_id
WHERE i.status = 'open';

COMMENT ON VIEW billing.v_invoice_aging IS $$
Open invoices (issued, not yet paid) with how many days past due they are and the aging bucket they fall in: current, 1-30, 31-60, or 61+ days overdue.
$$;
