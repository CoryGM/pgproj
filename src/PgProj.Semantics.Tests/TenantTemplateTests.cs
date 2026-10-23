using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-290: a tenant template's files carry a search path of their own (the tenant's schema, then the project's <c>searchPath</c>), so every object in that
/// schema finds an unqualified name the way PostgreSQL finds it in the session that deploys the tenant, while the shared project resolves as before; and
/// <see cref="TenantTemplateCheck"/> keeps the template to the tenant's own objects and the shared project out of the tenants.
/// </summary>
[TestClass]
public sealed class TenantTemplateTests
{
    private const string Prefix = "tenant";

    private const string Shared = """
        CREATE SCHEMA platform;
        CREATE TABLE platform.currencies (code char(3) PRIMARY KEY);
        CREATE FUNCTION platform.round_money(numeric) RETURNS numeric LANGUAGE sql IMMUTABLE RETURN round($1, 2);
        CREATE FUNCTION public.label() RETURNS text LANGUAGE sql IMMUTABLE RETURN 'shared';
        CREATE TABLE public.orders (id int);
        """;

    private const string Template = """
        CREATE TABLE orders (id bigint PRIMARY KEY, currency char(3) REFERENCES platform.currencies (code), total numeric);
        CREATE FUNCTION label() RETURNS text LANGUAGE sql IMMUTABLE SET search_path FROM CURRENT RETURN 'tenant';
        CREATE VIEW order_labels AS SELECT id, label(), round_money(total) AS total FROM orders;
        """;

    [TestMethod]
    public void TheTemplatesObjectsAreInTheStandInSchemaAndResolveAlongItsOwnPath()
    {
        var model = Build(Shared, Template);

        Assert.IsTrue(model.Tables.ContainsKey("tenant.orders"));
        CollectionAssert.AreEqual(new[] { "tenant", "platform", "public" }, model.SearchPathFor("tenant").ToArray());
        CollectionAssert.AreEqual(new[] { "platform", "public" }, model.SearchPathFor("platform").ToArray(), "a shared schema keeps the project's path");
        CollectionAssert.AreEqual(new[] { "platform", "public" }, model.SearchPathFor(null).ToArray());
        CollectionAssert.Contains(model.Views["tenant.order_labels"].ReferencedTables.ToArray(), "tenant.orders", "the template's unqualified orders is the tenant's, not public.orders");
        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Error), Describe(model));
    }

    [TestMethod]
    public void TheSharedProjectStillResolvesAlongTheProjectsPath()
    {
        var model = Build(Shared + "CREATE VIEW public.shared_orders AS SELECT id, label() FROM orders;", Template);

        CollectionAssert.Contains(model.Views["public.shared_orders"].ReferencedTables.ToArray(), "public.orders");
    }

    [TestMethod]
    public void ASharedReferenceThatOnlyTheTemplateDeclaresIsReportedAsOffThePath()
    {
        var model = Build(Shared + "CREATE VIEW public.counts AS SELECT count(*) FROM order_labels;", Template);

        StringAssert.Contains(model.Diagnostics.Single(static diagnostic => diagnostic.Source == "SearchPathValidationPass").Message, "relation 'order_labels'");
    }

    [TestMethod]
    public void ATemplateSequenceFindsItsOwnedByTableInTheTenant()
    {
        var model = Build(Shared, "CREATE TABLE orders (id int); CREATE SEQUENCE order_numbers; ALTER SEQUENCE order_numbers OWNED BY orders.id;");

        Assert.AreEqual("tenant.orders", model.Sequences["tenant.order_numbers"].ResolvedOwnedByTable);
    }

    [TestMethod]
    [DataRow("CREATE TABLE platform.audit (id int PRIMARY KEY, note text);", "Table platform.audit is in the shared schema 'platform'")]
    [DataRow("CREATE TABLE orders (id int); CREATE INDEX ON platform.currencies (code);", "Index", "is in the shared schema 'platform'")]
    [DataRow("CREATE TABLE tenant.extra (id int);", "Table tenant.extra is written with the schema 'tenant'")]
    [DataRow("CREATE TABLE tenant_acme.extra (id int);", "is written with the schema 'tenant_acme'")]
    [DataRow("CREATE TABLE a (id int); CREATE VIEW v AS SELECT * FROM tenant_acme.a;", "refers to an object in 'tenant_acme', which is one tenant's schema")]
    [DataRow("CREATE TABLE a (id int); CREATE VIEW v AS SELECT * FROM tenant.a;", "refers to an object written with the schema 'tenant', the stand-in")]
    [DataRow("CREATE TYPE mood AS ENUM ('ok'); CREATE TABLE a (m tenant.mood);", "refers to an object written with the schema 'tenant'")]
    [DataRow("CREATE TABLE a (id int); CREATE TABLE b (a_id int REFERENCES tenant_acme.a (id));", "refers to an object in 'tenant_acme'")]
    [DataRow("CREATE EXTENSION hstore;", "The extension hstore belongs to the whole database")]
    [DataRow("CREATE PUBLICATION p FOR ALL TABLES;", "The publication p belongs to the whole database")]
    [DataRow("GRANT app_admin TO app_user;", "A role membership")]
    [DataRow("ALTER DEFAULT PRIVILEGES GRANT SELECT ON TABLES TO PUBLIC;", "ALTER DEFAULT PRIVILEGES without IN SCHEMA")]
    [DataRow("GRANT USAGE ON SCHEMA platform TO PUBLIC;", "A grant on schema platform: the tenant template can name only its own schema")]
    [DataRow("GRANT SELECT ON ALL TABLES IN SCHEMA platform TO PUBLIC;", "ALL ... IN SCHEMA platform")]
    [DataRow("CREATE SCHEMA other;", "CREATE SCHEMA other")]
    [DataRow("COMMENT ON SCHEMA platform IS 'x';", "The comment on schema platform")]
    [DataRow("GRANT SELECT ON platform.currencies TO PUBLIC;", "A grant on currencies is in the shared schema 'platform'")]
    public void TheTemplateIsKeptToTheTenantsOwnObjects(string template, string expected, string? alsoExpected = null)
    {
        var errors = Errors(Shared, template);

        Assert.AreEqual(1, errors.Count, string.Join(" | ", errors));
        StringAssert.StartsWith(errors[0], "Tenant template: ");
        StringAssert.Contains(errors[0], expected);
        if (alsoExpected is not null)
        {
            StringAssert.Contains(errors[0], alsoExpected);
        }
    }

    [TestMethod]
    [DataRow("CREATE TABLE a (id int, c char(3) REFERENCES platform.currencies (code));")]
    [DataRow("CREATE TABLE a (id int); CREATE VIEW v AS SELECT platform.round_money(1), * FROM a;")]
    [DataRow("CREATE TABLE a (id int); GRANT SELECT ON a TO PUBLIC;")]
    [DataRow("GRANT USAGE ON SCHEMA tenant TO PUBLIC;")]
    [DataRow("GRANT SELECT ON ALL TABLES IN SCHEMA tenant TO PUBLIC;")]
    [DataRow("ALTER DEFAULT PRIVILEGES IN SCHEMA tenant GRANT SELECT ON TABLES TO PUBLIC;")]
    [DataRow("COMMENT ON SCHEMA tenant IS 'One customer''s data.';")]
    [DataRow("ALTER SCHEMA tenant OWNER TO app_owner;")]
    [DataRow("CREATE SCHEMA tenant;")]
    [DataRow("CREATE TABLE a (id int); COMMENT ON TABLE a IS 'x'; ALTER TABLE a OWNER TO app_owner;")]
    [DataRow("CREATE VIEW v AS SELECT tenant.id FROM platform.currencies AS tenant(id);")]
    public void WhatTheTemplateMayWriteIsAccepted(string template)
        => Assert.AreEqual(0, Errors(Shared, template).Count, string.Join(" | ", Errors(Shared, template)));

    [TestMethod]
    [DataRow("CREATE TABLE tenant_acme.extra (id int);", "Table tenant_acme.extra is in the schema 'tenant_acme', which is a tenant's schema")]
    [DataRow("CREATE TABLE tenant.extra (id int);", "is in the schema 'tenant', which is the tenant template's stand-in schema")]
    [DataRow("CREATE VIEW platform.all_orders AS SELECT * FROM tenant_acme.orders;", "refers to an object in the schema 'tenant_acme'")]
    [DataRow("GRANT USAGE ON SCHEMA tenant TO PUBLIC;", "A grant on schema tenant: 'tenant' is the tenant template's stand-in schema")]
    [DataRow("CREATE SCHEMA tenant_acme;", "'tenant_acme' is a tenant's schema")]
    public void TheSharedProjectCannotReachIntoTheTenants(string shared, string expected)
    {
        var errors = Errors(Shared + shared, string.Empty);

        Assert.AreEqual(1, errors.Count, string.Join(" | ", errors));
        StringAssert.Contains(errors[0], expected);
    }

    [TestMethod]
    public void ATemplateRoutineResolvedWhenItRunsWithoutItsOwnSearchPathIsAWarning()
    {
        var diagnostics = TenantTemplateCheck.Check(
            Parse(Shared),
            Parse(
                """
                CREATE TABLE a (id int);
                CREATE FUNCTION f() RETURNS bigint LANGUAGE sql AS $$ SELECT count(*) FROM a $$;
                CREATE FUNCTION g() RETURNS int LANGUAGE plpgsql AS $$ BEGIN RETURN 1; END $$;
                CREATE PROCEDURE p() LANGUAGE sql AS $$ DELETE FROM a $$;
                CREATE FUNCTION h() RETURNS int LANGUAGE plpgsql SET search_path FROM CURRENT AS $$ BEGIN RETURN 1; END $$;
                CREATE FUNCTION i() RETURNS bigint LANGUAGE sql BEGIN ATOMIC SELECT count(*) FROM a; END;
                CREATE FUNCTION j() RETURNS int LANGUAGE sql SET search_path = pg_catalog RETURN 1;
                CREATE FUNCTION k() RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;
                """,
                Prefix),
            Prefix);

        var warning = diagnostics.Single();
        Assert.AreEqual(SemanticDiagnosticSeverity.Warning, warning.Severity);
        StringAssert.Contains(warning.Message, "4 routine(s) (function f, function g, procedure p, and 1 more) do not set search_path");
        StringAssert.Contains(warning.Message, "SET search_path FROM CURRENT");
    }

    [TestMethod]
    public void ADiagnosticNamesTheFileItIsIn()
    {
        var template = Parse("CREATE TABLE platform.audit (id int);", Prefix).Select(static tree => tree with { FilePath = "sql/Tenant/audit.sql" }).ToArray();

        Assert.AreEqual("sql/Tenant/audit.sql", TenantTemplateCheck.Check(Parse(Shared), template, Prefix).Single().Location!.FilePath);
    }

    private static List<string> Errors(string shared, string template)
        => TenantTemplateCheck.Check(Parse(shared), Parse(template, Prefix), Prefix)
            .Where(static diagnostic => diagnostic.Severity == SemanticDiagnosticSeverity.Error)
            .Select(static diagnostic => diagnostic.Message)
            .ToList();

    private static DatabaseModel Build(string shared, string template)
        => new SemanticPipeline().Execute([.. Parse(shared), .. Parse(template, Prefix)], searchPath: ["platform", "public"]);

    private static IReadOnlyList<SqlSyntaxTree> Parse(string sql, string? schema = null)
        => schema is null
            ? [new PgQueryParser().Parse(sql)]
            : [new PgQueryParser(schema).Parse(sql) with { SearchPath = [schema, "platform", "public"] }];

    private static string Describe(DatabaseModel model) => string.Join(" | ", model.Diagnostics.Select(static diagnostic => diagnostic.Message));
}
