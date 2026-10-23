namespace PgProj.Parser.Tests;

/// <summary>G-290d: SQL a tenant's schema was scaffolded as becomes template SQL, by PostgreSQL's lexer, so comments and strings are not mistaken for names.</summary>
[TestClass]
public sealed class TenantSchemaRewriterTests
{
    [TestMethod]
    [DataRow("CREATE TABLE tenant_acme.orders (id int);", "CREATE TABLE orders (id int);")]
    [DataRow("CREATE TABLE \"tenant_acme\".\"Orders\" (id int);", "CREATE TABLE \"Orders\" (id int);")]
    [DataRow("ALTER TABLE ONLY tenant_acme.orders ADD CONSTRAINT orders_customer_id_fkey FOREIGN KEY (customer_id) REFERENCES tenant_acme.customers(id);",
        "ALTER TABLE ONLY orders ADD CONSTRAINT orders_customer_id_fkey FOREIGN KEY (customer_id) REFERENCES customers(id);")]
    [DataRow("ALTER TABLE tenant_acme.orders ADD CONSTRAINT c FOREIGN KEY (currency) REFERENCES platform.currencies(code);",
        "ALTER TABLE orders ADD CONSTRAINT c FOREIGN KEY (currency) REFERENCES platform.currencies(code);")]
    [DataRow("CREATE VIEW tenant_acme.v AS SELECT o.id FROM tenant_acme.orders o WHERE o.status = 'open'::tenant_acme.order_status;",
        "CREATE VIEW v AS SELECT o.id FROM orders o WHERE o.status = 'open'::order_status;")]
    [DataRow("CREATE TABLE tenant_acme.t (id bigint DEFAULT nextval('tenant_acme.t_id_seq'::regclass));", "CREATE TABLE t (id bigint DEFAULT nextval('t_id_seq'::regclass));")]
    [DataRow("GRANT USAGE ON SCHEMA tenant_acme TO PUBLIC;", "GRANT USAGE ON SCHEMA tenant TO PUBLIC;")]
    [DataRow("CREATE SCHEMA IF NOT EXISTS tenant_acme;\nALTER SCHEMA tenant_acme OWNER TO app;", "CREATE SCHEMA IF NOT EXISTS tenant;\nALTER SCHEMA tenant OWNER TO app;")]
    [DataRow("ALTER DEFAULT PRIVILEGES IN SCHEMA tenant_acme GRANT SELECT ON TABLES TO r;", "ALTER DEFAULT PRIVILEGES IN SCHEMA tenant GRANT SELECT ON TABLES TO r;")]
    [DataRow("COMMENT ON TABLE tenant_acme.orders IS 'Orders of tenant_acme.';", "COMMENT ON TABLE orders IS 'Orders of tenant_acme.';")]
    [DataRow("-- tenant_acme.orders\nCREATE TABLE tenant_acme_two.t (id int);", "-- tenant_acme.orders\nCREATE TABLE tenant_acme_two.t (id int);")]
    public void TheTenantsSchemaBecomesTheTemplates(string sql, string expected)
    {
        var result = TenantSchemaRewriter.Rewrite(sql, "tenant_acme", "tenant");

        Assert.AreEqual(expected, result.Text);
        Assert.IsFalse(result.NamesSchemaInBody);
    }

    [TestMethod]
    [DataRow(" SET search_path TO 'tenant_acme', 'platform', 'public'", " SET search_path TO 'tenant', 'platform', 'public'")]
    [DataRow(" SET search_path = tenant_acme, public", " SET search_path = tenant, public")]
    [DataRow(" SET search_path TO 'public', 'tenant_acme'", " SET search_path TO 'public', 'tenant'")]
    [DataRow(" SET work_mem TO '64MB' SET search_path TO 'tenant_acme' AS $$ SELECT 'tenant_acme' $$", " SET work_mem TO '64MB' SET search_path TO 'tenant' AS $$ SELECT 'tenant_acme' $$")]
    public void ASearchPathSettingNamesTheStandIn(string sql, string expected)
        => Assert.AreEqual(expected, TenantSchemaRewriter.Rewrite(sql, "tenant_acme", "tenant").Text);

    [TestMethod]
    [DataRow("SET search_path TO 'tenant', 'platform', 'public'", "SET search_path TO 'tenant_globex', 'platform', 'public'")]
    [DataRow("SET search_path = tenant, public", "SET search_path = tenant_globex, public")]
    [DataRow("SET search_path TO 'platform'", "SET search_path TO 'platform'")]
    public void EachTenantsCopyReadsTheStandInAsItsOwnSchema(string text, string expected)
        => Assert.AreEqual(expected, TenantSchemaRewriter.MapStandIn(text, "tenant", "tenant_globex"));

    [TestMethod]
    public void ABodyThatStillNamesTheSchemaIsReportedAndLeftAsWritten()
    {
        const string Sql = "CREATE FUNCTION tenant_acme.f() RETURNS bigint LANGUAGE sql AS $$ SELECT count(*) FROM tenant_acme.orders $$;";

        var result = TenantSchemaRewriter.Rewrite(Sql, "tenant_acme", "tenant");

        Assert.AreEqual("CREATE FUNCTION f() RETURNS bigint LANGUAGE sql AS $$ SELECT count(*) FROM tenant_acme.orders $$;", result.Text);
        Assert.IsTrue(result.NamesSchemaInBody);
    }

    [TestMethod]
    public void ABodyThatDoesNotNameItIsNotReported()
        => Assert.IsFalse(TenantSchemaRewriter.Rewrite("CREATE FUNCTION tenant_acme.f() RETURNS bigint LANGUAGE sql AS $$ SELECT count(*) FROM orders $$;", "tenant_acme", "tenant").NamesSchemaInBody);
}
