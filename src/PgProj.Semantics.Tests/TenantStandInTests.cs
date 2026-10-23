using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-290b: where the template names the schema itself, it names the stand-in (the prefix), and each tenant's copy takes the tenant's schema there;
/// everything else in the template is unqualified and already takes the tenant's schema as its default.
/// </summary>
[TestClass]
public sealed class TenantStandInTests
{
    [TestMethod]
    public void EveryStatementAboutTheSchemaItselfTakesTheTenantsSchema()
    {
        const string Template = """
            CREATE SCHEMA tenant;
            CREATE TABLE orders (id int);
            GRANT USAGE ON SCHEMA tenant TO app_user;
            GRANT SELECT ON ALL TABLES IN SCHEMA tenant TO app_reader;
            ALTER DEFAULT PRIVILEGES IN SCHEMA tenant GRANT SELECT ON TABLES TO app_reader;
            ALTER SCHEMA tenant OWNER TO app_owner;
            COMMENT ON SCHEMA tenant IS 'One customer''s data.';
            """;

        var nodes = TenantStandIn.Replace([new PgQueryParser("tenant_acme").Parse(Template)], "tenant", "tenant_acme").Single().Nodes;

        Assert.AreEqual("tenant_acme", nodes.OfType<SchemaNode>().Single().Name);
        Assert.AreEqual("tenant_acme", nodes.OfType<TableNode>().Single().SchemaName, "a statement written without a schema already had the tenant's");
        var grants = nodes.OfType<GrantNode>().ToArray();
        Assert.AreEqual("tenant_acme", grants.Single(static grant => grant.Form == GrantForm.Object).Targets.Single().Name);
        Assert.AreEqual("tenant_acme", grants.Single(static grant => grant.Form == GrantForm.AllInSchema).Targets.Single().Name);
        CollectionAssert.AreEqual(new[] { "tenant_acme" }, grants.Single(static grant => grant.Form == GrantForm.DefaultPrivileges).Schemas.ToArray());
        Assert.AreEqual("tenant_acme", nodes.OfType<OwnerNode>().Single().ObjectName);
        var comment = nodes.OfType<ObjectCommentNode>().Single();
        Assert.AreEqual("tenant_acme", comment.SchemaName);
        Assert.AreEqual("tenant_acme", comment.TableName);
    }

    [TestMethod]
    public void AModelOfTheTenantHasTheGrantAndCommentOnItsOwnSchema()
    {
        var trees = TenantStandIn.Replace(
            [new PgQueryParser("tenant_acme").Parse("GRANT USAGE ON SCHEMA tenant TO PUBLIC; COMMENT ON SCHEMA tenant IS 'x';") with { SearchPath = ["tenant_acme", "public"] }],
            "tenant",
            "tenant_acme");

        var model = new SemanticPipeline().Execute(trees);

        Assert.IsTrue(model.Grants.Values.Any(static grant => grant.SchemaName == "tenant_acme"), string.Join(", ", model.Grants.Keys));
        Assert.IsFalse(model.Grants.Values.Any(static grant => grant.SchemaName == "tenant"));
        Assert.AreEqual("x", model.SchemaDefinitions.Values.Single(static schema => schema.Name == "tenant_acme").Comment);
    }

    [TestMethod]
    public void ARoutinesSearchPathNamingTheStandInIsTheTenants()
    {
        const string Template = "CREATE FUNCTION f() RETURNS bigint LANGUAGE sql SET search_path TO 'tenant', 'platform' AS $$ SELECT 1 $$;";

        var function = TenantStandIn.Replace([new PgQueryParser("tenant_acme").Parse(Template)], "tenant", "tenant_acme").Single().Nodes.OfType<FunctionNode>().Single();

        var setting = function.Signature!.Settings.Single();
        CollectionAssert.AreEqual(new[] { "tenant_acme", "platform" }, setting.Values.ToArray());
        Assert.AreEqual("SET search_path TO 'tenant_acme', 'platform'", setting.Render());
    }

    [TestMethod]
    public void AnotherSchemaIsLeftAsWritten()
    {
        var nodes = TenantStandIn.Replace([new PgQueryParser("tenant_acme").Parse("GRANT USAGE ON SCHEMA platform TO app_user;")], "tenant", "tenant_acme").Single().Nodes;

        Assert.AreEqual("platform", nodes.OfType<GrantNode>().Single().Targets.Single().Name);
    }
}
