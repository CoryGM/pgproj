CREATE DOMAIN money_amount AS numeric(12, 2) CHECK (VALUE >= 0);
COMMENT ON DOMAIN money_amount IS 'A price or total: two decimals, never negative.';
