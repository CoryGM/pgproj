using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-116: PostgreSQL drops an object's privileges with it. A grant that is unchanged between the project and the database has no
/// operation of its own, so an object the publish drops and recreates (a materialized view whose query changed, a function whose return
/// type changed) came back without its grants. The project's grants on it are now issued again right after it is created, and a
/// project that leaves grants out keeps the privileges the database had.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RecreatedObjectPrivilegeTests
{
    private const string Schema = "CREATE SCHEMA s; CREATE TABLE s.c (id int PRIMARY KEY, n int);";

    [TestMethod]
    public void ARecreatedMaterializedViewGetsItsGrantsBack()
    {
        var grants = " GRANT SELECT ON s.m TO rdr; COMMENT ON MATERIALIZED VIEW s.m IS 'totals';";
        var statements = Statements(
            Build(Schema + " CREATE MATERIALIZED VIEW s.m AS SELECT id FROM s.c;" + grants),
            Build(Schema + " CREATE MATERIALIZED VIEW s.m AS SELECT id, n FROM s.c;" + grants));

        var create = statements.FindIndex(static statement => statement.StartsWith("CREATE MATERIALIZED VIEW", StringComparison.Ordinal));
        var grant = statements.IndexOf("GRANT SELECT ON TABLE \"s\".\"m\" TO \"rdr\";");
        Assert.IsGreaterThanOrEqualTo(0, create, string.Join("\n", statements));
        Assert.IsGreaterThan(create, grant, string.Join("\n", statements));
    }

    [TestMethod]
    public void AFunctionRecreatedForANewReturnTypeGetsItsGrantsBack()
    {
        var statements = Statements(
            Build("CREATE SCHEMA s; CREATE FUNCTION s.f(p int) RETURNS int LANGUAGE sql AS $$ SELECT p $$; GRANT EXECUTE ON FUNCTION s.f(int) TO rdr;"),
            Build("CREATE SCHEMA s; CREATE FUNCTION s.f(p int) RETURNS bigint LANGUAGE sql AS $$ SELECT p::bigint $$; GRANT EXECUTE ON FUNCTION s.f(int) TO rdr;"));

        var create = statements.FindIndex(static statement => statement.StartsWith("CREATE OR REPLACE FUNCTION", StringComparison.Ordinal));
        var grant = statements.FindIndex(static statement => statement.StartsWith("GRANT", StringComparison.Ordinal) && statement.Contains("\"s\".\"f\"", StringComparison.Ordinal));
        Assert.IsGreaterThan(create, grant, string.Join("\n", statements));
    }

    [TestMethod]
    public void AGrantTheProjectDropsIsNotIssuedAgain()
    {
        var statements = Statements(
            Build(Schema + " CREATE MATERIALIZED VIEW s.m AS SELECT id FROM s.c; GRANT SELECT ON s.m TO rdr;"),
            Build(Schema + " CREATE MATERIALIZED VIEW s.m AS SELECT id, n FROM s.c;"));

        Assert.IsFalse(statements.Any(static statement => statement.StartsWith("GRANT", StringComparison.Ordinal)), string.Join("\n", statements));
    }

    [TestMethod]
    public void AGrantAddedInTheSamePublishIsIssuedOnce()
    {
        var statements = Statements(
            Build(Schema + " CREATE MATERIALIZED VIEW s.m AS SELECT id FROM s.c;"),
            Build(Schema + " CREATE MATERIALIZED VIEW s.m AS SELECT id, n FROM s.c; GRANT SELECT ON s.m TO rdr;"));

        Assert.AreEqual(1, statements.Count(static statement => statement.StartsWith("GRANT", StringComparison.Ordinal)), string.Join("\n", statements));
    }

    [TestMethod]
    public void AnObjectThatIsNotRecreatedIsNotRegranted()
    {
        var statements = Statements(
            Build(Schema + " CREATE VIEW s.v AS SELECT id FROM s.c; GRANT SELECT ON s.v TO rdr;"),
            Build(Schema + " CREATE VIEW s.v AS SELECT id, n FROM s.c; GRANT SELECT ON s.v TO rdr;"));

        Assert.IsTrue(statements.Any(static statement => statement.StartsWith("CREATE OR REPLACE VIEW", StringComparison.Ordinal)));
        Assert.IsFalse(statements.Any(static statement => statement.StartsWith("GRANT", StringComparison.Ordinal)), string.Join("\n", statements));
    }

    [TestMethod]
    public void WithoutModeledGrantsTheDatabasesPrivilegesAreRestored()
    {
        var live = Build(Schema + " CREATE MATERIALIZED VIEW s.m AS SELECT id FROM s.c;");
        live.UnmodeledPrivileges =
        [
            new UnmodeledPrivilege("table", "s", "m", "GRANT SELECT ON TABLE s.m TO rdr"),
            new UnmodeledPrivilege("table", "s", null, "GRANT UPDATE ON ALL TABLES IN SCHEMA s TO rdr"),
            new UnmodeledPrivilege("table", "s", "c", "GRANT SELECT ON TABLE s.c TO rdr"),
            new UnmodeledPrivilege("function", "s", "m", "GRANT EXECUTE ON FUNCTION s.m() TO rdr"),
        ];

        var operations = Operations(live, Build(Schema + " CREATE MATERIALIZED VIEW s.m AS SELECT id, n FROM s.c;"));
        var statements = operations.Select(static operation => operation.GenerateSql()).ToList();

        var restored = operations.OfType<PreservedStatementOperation>().ToArray();
        Assert.HasCount(2, restored, string.Join("\n", statements));
        StringAssert.Contains(restored[0].Reason, "s.m was dropped and recreated above");
        Assert.IsTrue(statements.Any(static statement => statement.EndsWith("GRANT SELECT ON TABLE s.m TO rdr;", StringComparison.Ordinal)));
        Assert.IsTrue(statements.Any(static statement => statement.EndsWith("GRANT UPDATE ON ALL TABLES IN SCHEMA s TO rdr;", StringComparison.Ordinal)));
    }

    private static IReadOnlyList<SqlOperation> Operations(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel)).Operations;

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel)
    {
        return Operations(oldModel, newModel)
            .Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .ToList();
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
