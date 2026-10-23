CREATE FUNCTION catalog.record_price_history()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    -- Nothing to record when an update touched unit_price without changing it.
    IF TG_OP = 'UPDATE' AND NEW.unit_price = OLD.unit_price THEN
        RETURN NEW;
    END IF;

    -- Close the price that was current, then open the new one.
    UPDATE catalog.price_history
    SET valid_to_utc = public.utc_now()
    WHERE product_id = NEW.id
      AND valid_to_utc IS NULL;

    INSERT INTO catalog.price_history (product_id, unit_price)
    VALUES (NEW.id, NEW.unit_price);

    RETURN NEW;
END;
$$;

COMMENT ON FUNCTION catalog.record_price_history() IS $$
Trigger function for catalog.product: when a product is created or its unit_price changes, closes the open catalog.price_history row and opens a new one at the new price.
$$;
