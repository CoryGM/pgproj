CREATE FUNCTION public.utc_now() 
RETURNS timestamp without time zone
LANGUAGE sql
AS $$
	SELECT now() AT TIME ZONE 'UTC';
$$;