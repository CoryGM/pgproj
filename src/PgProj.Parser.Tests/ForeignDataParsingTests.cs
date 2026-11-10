namespace PgProj.Parser.Tests;

/// <summary>
/// G-132: <c>CREATE FOREIGN DATA WRAPPER</c>, <c>CREATE SERVER</c>, and <c>CREATE USER MAPPING</c> are read, not reported; an option value may be a variable,
/// <c>:'name'</c>, in a server or a mapping (G-336's designated slots), and anywhere else a variable is an error; owners and comments of wrappers and servers are read.
/// </summary>
[TestClass]
public sealed class ForeignDataParsingTests
{
    [TestMethod]
    public void AWrapperIsReadWithItsFunctionsAndOptions()
    {
        var tree = new PgQueryParser().Parse("CREATE FOREIGN DATA WRAPPER w HANDLER app.h VALIDATOR v OPTIONS (debug 'true'); CREATE FOREIGN DATA WRAPPER w2 NO HANDLER;");

        var wrappers = tree.Nodes.OfType<ForeignDataWrapperNode>().ToArray();
        Assert.AreEqual("app.h", wrappers[0].Handler);
        Assert.AreEqual("v", wrappers[0].Validator);
        Assert.AreEqual("debug=true", string.Join(",", wrappers[0].Options.Select(static option => $"{option.Name}={option.Value}")));
        Assert.IsNull(wrappers[1].Handler);
        Assert.IsEmpty(tree.Diagnostics);
    }

    [TestMethod]
    public void AServerAndMappingsAreReadWithAVariableKeepingItsName()
    {
        var tree = new PgQueryParser().Parse("""
            CREATE SERVER s TYPE 'pg' VERSION '18' FOREIGN DATA WRAPPER postgres_fdw OPTIONS (host :'db_host', port '5432');
            CREATE USER MAPPING FOR app_user SERVER s OPTIONS (user 'u', password :'pw');
            CREATE USER MAPPING FOR PUBLIC SERVER s;
            CREATE USER MAPPING FOR CURRENT_USER SERVER s;
            """);

        var server = tree.Nodes.OfType<ForeignServerNode>().Single();
        Assert.AreEqual("postgres_fdw", server.Wrapper);
        Assert.AreEqual("pg", server.ServerType);
        Assert.AreEqual("18", server.Version);
        Assert.AreEqual("db_host", server.Options[0].Variable);
        Assert.IsNull(server.Options[1].Variable);
        CollectionAssert.AreEqual(new[] { "app_user", "PUBLIC", UserMappingNode.CurrentUser }, tree.Nodes.OfType<UserMappingNode>().Select(static mapping => mapping.Role).ToArray());
        Assert.AreEqual("pw", tree.Nodes.OfType<UserMappingNode>().First().Options[1].Variable);
        Assert.IsEmpty(tree.Diagnostics);
    }

    [TestMethod]
    [DataRow("CREATE TABLE t (c text DEFAULT :'x');")]
    [DataRow("CREATE FOREIGN DATA WRAPPER w OPTIONS (debug :'x');")]
    [DataRow("COMMENT ON TABLE t IS :'x';")]
    public void AVariableOutsideAServerOrMappingsOptionsIsAnError(string sql)
    {
        var tree = new PgQueryParser().Parse("CREATE TABLE t (id int);\n" + sql);

        StringAssert.StartsWith(tree.Diagnostics.Single(static diagnostic => diagnostic.Severity == SqlDiagnosticSeverity.Error).Message, "A variable (:'name') is allowed only as an OPTIONS value of CREATE SERVER or CREATE USER MAPPING");
    }

    [TestMethod]
    public void AVariableInsideAStringOrABodyIsNotOne()
    {
        var tree = new PgQueryParser().Parse("COMMENT ON TABLE t IS 'say :''x'''; CREATE FUNCTION f() RETURNS text LANGUAGE sql AS $$ SELECT ':''y''' $$;");

        Assert.IsFalse(tree.Diagnostics.Any(static diagnostic => diagnostic.Severity == SqlDiagnosticSeverity.Error), string.Join(" | ", tree.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void OwnersAndCommentsOfWrappersAndServersAreRead()
    {
        var tree = new PgQueryParser().Parse("ALTER SERVER s OWNER TO bob; ALTER FOREIGN DATA WRAPPER w OWNER TO bob; COMMENT ON SERVER s IS 'x'; COMMENT ON FOREIGN DATA WRAPPER w IS 'y';");

        CollectionAssert.AreEqual(new[] { "ForeignServer", "ForeignDataWrapper" }, tree.Owners.Select(static owner => owner.ObjectKind).ToArray());
        CollectionAssert.AreEqual(new[] { "ForeignServer", "ForeignDataWrapper" }, tree.ObjectComments.Select(static comment => comment.ObjectKind).ToArray());
        Assert.IsEmpty(tree.Diagnostics);
    }

    [TestMethod]
    [DataRow("ALTER SERVER s OPTIONS (SET host 'x');", "ALTER SERVER")]
    [DataRow("ALTER USER MAPPING FOR u SERVER s OPTIONS (SET user 'x');", "ALTER USER MAPPING")]
    [DataRow("ALTER FOREIGN DATA WRAPPER w OPTIONS (DROP debug);", "ALTER FOREIGN DATA WRAPPER")]
    public void TheAlterFormsAreStillReported(string sql, string kind)
    {
        StringAssert.StartsWith(new PgQueryParser().Parse(sql).Diagnostics.Single().Message, kind + " is not modeled");
    }
}
