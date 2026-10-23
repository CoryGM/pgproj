CREATE FUNCTION touch_updated_at() RETURNS trigger
    LANGUAGE plpgsql
    SET search_path FROM CURRENT
AS $$
BEGIN
    NEW.updated_at := now();
    RETURN NEW;
END
$$;
COMMENT ON FUNCTION touch_updated_at() IS 'Keeps updated_at current on products and orders.';
