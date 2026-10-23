-- No marker: runs on every publish, so it must be safe to run again (ON CONFLICT DO NOTHING).
INSERT INTO platform.currencies (code, name, minor_units) VALUES
    ('USD', 'US dollar', 2), ('EUR', 'Euro', 2), ('GBP', 'Pound sterling', 2), ('JPY', 'Yen', 0)
ON CONFLICT (code) DO NOTHING;

INSERT INTO platform.countries (code, name, currency) VALUES
    ('US', 'United States', 'USD'), ('DE', 'Germany', 'EUR'), ('GB', 'United Kingdom', 'GBP'), ('JP', 'Japan', 'JPY')
ON CONFLICT (code) DO NOTHING;

INSERT INTO platform.plans (code, name, monthly_price, max_products) VALUES
    ('basic', 'Basic', 29.00, 500), ('pro', 'Pro', 99.00, NULL)
ON CONFLICT (code) DO NOTHING;

-- The shops tenants.csv lists, for an application (or a "query" in pgproj.json) to read.
INSERT INTO platform.shops (slug, display_name, plan) VALUES
    ('acme', 'Acme Hardware', 'pro'), ('globex', 'Globex Garden Supply', 'basic'), ('initech', 'Initech Office Store', 'basic')
ON CONFLICT (slug) DO NOTHING;
