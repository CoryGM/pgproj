namespace PgProj.Integration.Tests;

/// <summary>
/// Representative multi-object SQL fixtures used by the G-30 live PostgreSQL integration
/// suite. Each fixture is scoped to a caller-supplied schema name so multiple tests can run
/// against the same shared container without colliding.
/// </summary>
internal static class SqlFixtures
{
    /// <summary>Initial schema: table with PK, a sequence-backed identity column, an FK-linked child table, an index, a view, and a function.</summary>
    public static string InitialSchema(string schema) => $"""
        CREATE SCHEMA {schema};

        CREATE TABLE {schema}.customers (
            id bigint GENERATED ALWAYS AS IDENTITY,
            name text NOT NULL,
            email text NOT NULL,
            CONSTRAINT customers_pkey PRIMARY KEY (id),
            CONSTRAINT customers_email_key UNIQUE (email)
        );

        CREATE TABLE {schema}.orders (
            id bigint GENERATED ALWAYS AS IDENTITY,
            customer_id bigint NOT NULL,
            total numeric(10,2) NOT NULL,
            CONSTRAINT orders_pkey PRIMARY KEY (id),
            CONSTRAINT orders_customer_id_fkey FOREIGN KEY (customer_id) REFERENCES {schema}.customers (id)
        );

        CREATE INDEX orders_customer_id_idx ON {schema}.orders (customer_id);

        CREATE VIEW {schema}.customer_totals AS
            SELECT c.id AS customer_id, c.name, sum(o.total) AS total
            FROM {schema}.customers c
            JOIN {schema}.orders o ON o.customer_id = c.id
            GROUP BY c.id, c.name;

        CREATE FUNCTION {schema}.customer_count() RETURNS integer
            LANGUAGE sql
            AS $$ SELECT count(*)::integer FROM {schema}.customers; $$;
        """;

    /// <summary>Revised schema: adds a column to customers, adds an index on it, and drops the email uniqueness in favor of a differently-named constraint.</summary>
    public static string RevisedSchema(string schema) => $"""
        CREATE SCHEMA {schema};

        CREATE TABLE {schema}.customers (
            id bigint GENERATED ALWAYS AS IDENTITY,
            name text NOT NULL,
            email text NOT NULL,
            signup_date date,
            CONSTRAINT customers_pkey PRIMARY KEY (id),
            CONSTRAINT customers_email_key UNIQUE (email)
        );

        CREATE TABLE {schema}.orders (
            id bigint GENERATED ALWAYS AS IDENTITY,
            customer_id bigint NOT NULL,
            total numeric(10,2) NOT NULL,
            CONSTRAINT orders_pkey PRIMARY KEY (id),
            CONSTRAINT orders_customer_id_fkey FOREIGN KEY (customer_id) REFERENCES {schema}.customers (id)
        );

        CREATE INDEX orders_customer_id_idx ON {schema}.orders (customer_id);
        CREATE INDEX customers_signup_date_idx ON {schema}.customers (signup_date);

        CREATE VIEW {schema}.customer_totals AS
            SELECT c.id AS customer_id, c.name, sum(o.total) AS total
            FROM {schema}.customers c
            JOIN {schema}.orders o ON o.customer_id = c.id
            GROUP BY c.id, c.name;

        CREATE FUNCTION {schema}.customer_count() RETURNS integer
            LANGUAGE sql
            AS $$ SELECT count(*)::integer FROM {schema}.customers; $$;
        """;
}
