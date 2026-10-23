namespace PgProj.Parser.Tests;

/// <summary>G-284: each <c>CREATE SCHEMA</c> gives a <see cref="SchemaNode"/> naming the schema, so a schema with nothing in it is still declared.</summary>
[TestClass]
public sealed class SchemaNodeTests
{
    [TestMethod]
    [DataRow("CREATE SCHEMA a;", "a")]
    [DataRow("CREATE SCHEMA IF NOT EXISTS A;", "a")]
    [DataRow("CREATE SCHEMA \"Mixed\";", "Mixed")]
    [DataRow("CREATE SCHEMA a AUTHORIZATION app_owner;", "a")]
    [DataRow("CREATE SCHEMA AUTHORIZATION app_owner;", "app_owner")]
    [DataRow("CREATE SCHEMA a CREATE TABLE t (id int);", "a")]
    public void ACreateSchemaDeclaresTheSchemaItNames(string sql, string expected)
    {
        var schema = new PgQueryParser().Parse(sql).Schemas.Single();

        Assert.AreEqual(expected, schema.Name);
        Assert.IsNull(schema.SchemaName, "A schema is in no schema, so the default schema is not applied to it.");
    }

    [TestMethod]
    public void AnAuthorizationOfCurrentUserWithNoNameDeclaresNothing()
    {
        Assert.AreEqual(0, new PgQueryParser().Parse("CREATE SCHEMA AUTHORIZATION CURRENT_USER;").Schemas.Count());
    }

    [TestMethod]
    public void OtherStatementsDeclareNoSchema()
    {
        Assert.AreEqual(0, new PgQueryParser().Parse("CREATE TABLE app.t (id int); ALTER SCHEMA app OWNER TO r; COMMENT ON SCHEMA app IS 'x';").Schemas.Count());
    }
}
