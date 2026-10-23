CREATE FUNCTION platform.format_money(amount numeric, currency char(3))
    RETURNS text
    LANGUAGE sql
    STABLE
    RETURN currency || ' ' || to_char(amount, 'FM999G999G990D00');
COMMENT ON FUNCTION platform.format_money(numeric, char) IS 'An amount with its currency code, for display.';
