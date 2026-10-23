CREATE PROCEDURE reporting.refresh_materialized_views()
LANGUAGE plpgsql
AS $$
DECLARE
    v_view regclass;
BEGIN
    -- CONCURRENTLY keeps each view readable while it rebuilds; every reporting view has the unique index it needs.
    FOREACH v_view IN ARRAY ARRAY[
        'reporting.mv_daily_sales'::regclass,
        'reporting.mv_top_products'::regclass,
        'reporting.mv_customer_lifetime_value'::regclass,
        'reporting.mv_low_stock'::regclass,
        'reporting.mv_monthly_recurring_revenue'::regclass
    ]
    LOOP
        EXECUTE format('REFRESH MATERIALIZED VIEW CONCURRENTLY %s', v_view);
    END LOOP;
END;
$$;

COMMENT ON PROCEDURE reporting.refresh_materialized_views() IS $$
Refreshes every materialized view in the reporting schema without blocking readers. Schedule it (for example with pg_cron) or call it after a batch of changes.

@example: call reporting.refresh_materialized_views()
$$;
