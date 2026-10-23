CREATE TABLE platform.currencies (
    code char(3) PRIMARY KEY,
    name text NOT NULL,
    minor_units smallint NOT NULL DEFAULT 2 CHECK (minor_units BETWEEN 0 AND 4)
);
COMMENT ON TABLE platform.currencies IS 'ISO 4217 currencies a shop can sell in. Filled by scripts/seed-platform.sql.';
