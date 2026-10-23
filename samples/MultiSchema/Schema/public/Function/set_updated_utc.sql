CREATE FUNCTION public.set_updated_utc()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    NEW.updated_utc := public.utc_now();
    RETURN NEW;
END;
$$;

COMMENT ON FUNCTION public.set_updated_utc() IS $$
Trigger function that stamps updated_utc with the current UTC time. Attached as a BEFORE UPDATE row trigger to every table that has audit columns.
$$;
