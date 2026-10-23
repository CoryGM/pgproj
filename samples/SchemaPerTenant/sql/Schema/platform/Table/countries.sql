CREATE TABLE platform.countries (
    code char(2) PRIMARY KEY,
    name text NOT NULL,
    currency char(3) NOT NULL REFERENCES platform.currencies (code)
);
COMMENT ON TABLE platform.countries IS 'ISO 3166 countries a shop can ship to, with the currency used there.';
