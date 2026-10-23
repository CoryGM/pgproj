using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-103: PostgreSQL won't change a column's type, or drop the column, while a view uses it. The live database's dependencies say which
/// views use which columns; those views, and the views built on them, are dropped before the change (dependent first) and recreated after
/// it, with a reason in the script. One the project doesn't have is refused instead.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ViewDependentColumnChangeTests
{
    private const string Table = "CREATE SCHEMA s; CREATE TABLE s.c (id int PRIMARY KEY, name text NOT NULL, score int);";
    private const string Views = " CREATE VIEW s.v AS SELECT c.id, c.score FROM s.c c; CREATE VIEW s.v2 AS SELECT id FROM s.v; CREATE VIEW s.names AS SELECT id, name FROM s.c;";

    private static readonly LiveDependency[] Dependencies =
    [
        new("view", "s.v", "s.c", "id"),
        new("view", "s.v", "s.c", "score"),
        new("view", "s.v2", "s.v", "id"),
        new("view", "s.names", "s.c", "id"),
        new("view", "s.names", "s.c", "name"),
    ];

    [TestMethod]
    public void TheViewsUsingARetypedColumnAreDroppedBeforeAndRecreatedAfter()
    {
        var statements = Statements(Live(Table + Views), Build(Table.Replace("score int", "score bigint", StringComparison.Ordinal) + Views));

        var dropV2 = statements.IndexOf("DROP VIEW \"s\".\"v2\";");
        var dropV = statements.IndexOf("DROP VIEW \"s\".\"v\";");
        var alter = statements.FindIndex(static statement => statement.Contains("ALTER COLUMN \"score\" TYPE bigint", StringComparison.Ordinal));
        var createV = statements.FindIndex(static statement => statement.StartsWith("CREATE OR REPLACE VIEW \"s\".\"v\" ", StringComparison.Ordinal));
        var createV2 = statements.FindIndex(static statement => statement.StartsWith("CREATE OR REPLACE VIEW \"s\".\"v2\"", StringComparison.Ordinal));
        var order = string.Join("\n", statements);
        Assert.IsTrue(dropV2 >= 0 && dropV2 < dropV && dropV < alter && alter < createV && createV < createV2, order);
        Assert.IsFalse(statements.Any(static statement => statement.Contains("\"s\".\"names\"", StringComparison.Ordinal)), "A view that doesn't use the column is left alone.\n" + order);
    }

    [TestMethod]
    public void EachDropAndRecreateSaysWhy()
    {
        var operations = Operations(Live(Table + Views), Build(Table.Replace("score int", "score bigint", StringComparison.Ordinal) + Views));

        var dropV = operations.OfType<DropOperation>().Single(static operation => operation.NodeId == "s.v");
        StringAssert.Contains(dropV.Reason, "uses column s.c.score");
        var dropV2 = operations.OfType<DropOperation>().Single(static operation => operation.NodeId == "s.v2");
        StringAssert.Contains(dropV2.Reason, "uses s.v");
        Assert.IsNotNull(operations.OfType<CreateOperation>().Single(static operation => operation.NodeId == "s.v").Reason);
    }

    [TestMethod]
    public void AViewRewrittenAwayFromADroppedColumnIsDroppedBeforeTheColumn()
    {
        var statements = Statements(
            Live(Table + " CREATE VIEW s.v AS SELECT c.id, c.score FROM s.c c;"),
            Build("CREATE SCHEMA s; CREATE TABLE s.c (id int PRIMARY KEY, name text NOT NULL); CREATE VIEW s.v AS SELECT c.id, c.name FROM s.c c;"));

        var dropView = statements.IndexOf("DROP VIEW \"s\".\"v\";");
        var dropColumn = statements.FindIndex(static statement => statement.Contains("DROP COLUMN \"score\"", StringComparison.Ordinal));
        var createView = statements.FindIndex(static statement => statement.StartsWith("CREATE OR REPLACE VIEW \"s\".\"v\"", StringComparison.Ordinal));
        Assert.IsTrue(dropView >= 0 && dropView < dropColumn && dropColumn < createView, string.Join("\n", statements));
    }

    [TestMethod]
    public void ADependentTheProjectDoesNotHaveIsRefused()
    {
        // Another project's view: outside this project's schemas, so only the database's dependency record names it.
        var live = Live(Table);
        live.LiveDependencies = [new LiveDependency("view", "other.report", "s.c", "score")];

        var batch = Batch(live, Build(Table.Replace("score int", "score bigint", StringComparison.Ordinal)));

        var blocked = batch.GetUnsupportedOperations().Single();
        Assert.AreEqual("other.report", blocked.NodeId);
        StringAssert.Contains(blocked.GenerateSql(), "not in this project");
    }

    [TestMethod]
    public void ARecreatedViewKeepsAnotherRolesOwnership()
    {
        var live = Live(Table + Views);
        live.LiveOwners = new Dictionary<string, string> { ["s.v"] = "deployer", ["s.v2"] = "reporting" };
        live.ConnectedRole = "deployer";

        var statements = Statements(live, Build(Table.Replace("score int", "score bigint", StringComparison.Ordinal) + Views));

        CollectionAssert.Contains(statements, "ALTER VIEW \"s\".\"v2\" OWNER TO \"reporting\";");
        Assert.IsFalse(statements.Any(static statement => statement.StartsWith("ALTER VIEW \"s\".\"v\" OWNER", StringComparison.Ordinal)), "Owned by the deploying role already.");
    }

    [TestMethod]
    public void ARecreatedMaterializedViewIsDestructive()
    {
        var live = Live(Table + " CREATE MATERIALIZED VIEW s.mv AS SELECT id, score FROM s.c;");
        live.LiveDependencies = [new LiveDependency("materialized view", "s.mv", "s.c", "score")];

        var batch = Batch(live, Build(Table.Replace("score int", "score bigint", StringComparison.Ordinal) + " CREATE MATERIALIZED VIEW s.mv AS SELECT id, score FROM s.c;"));

        Assert.IsTrue(batch.GetDestructiveOperations().Any(static operation => operation.Operation.NodeId == "s.mv"));
    }

    [TestMethod]
    public void AChangeNoViewIsInTheWayOfIsUnchanged()
    {
        var statements = Statements(Live(Table + Views), Build(Table.Replace("name text NOT NULL", "name text", StringComparison.Ordinal) + Views));

        Assert.IsFalse(statements.Any(static statement => statement.StartsWith("DROP VIEW", StringComparison.Ordinal)), string.Join("\n", statements));
    }

    [TestMethod]
    public void APolicyAndATriggerUsingTheColumnAreDroppedAndRecreatedAroundIt()
    {
        // Part 2: PostgreSQL refuses a column change under a policy, and under a trigger whose WHEN or UPDATE OF uses the column.
        const string Rest = " ALTER TABLE s.c ENABLE ROW LEVEL SECURITY; CREATE POLICY p ON s.c USING (score > 0);"
            + " CREATE FUNCTION s.tf() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;"
            + " CREATE TRIGGER tr BEFORE UPDATE OF score ON s.c FOR EACH ROW EXECUTE FUNCTION s.tf();"
            + " CREATE TRIGGER other BEFORE INSERT ON s.c FOR EACH ROW EXECUTE FUNCTION s.tf();";
        var live = Build(Table + Rest);
        live.LiveDependencies = [new LiveDependency("policy", "s.c.p", "s.c", "score"), new LiveDependency("trigger", "s.c.tr", "s.c", "score")];

        var statements = Statements(live, Build(Table.Replace("score int", "score bigint", StringComparison.Ordinal) + Rest));

        var alter = statements.FindIndex(static statement => statement.Contains("TYPE bigint", StringComparison.Ordinal));
        Assert.IsLessThan(alter, statements.IndexOf("DROP POLICY \"p\" ON \"s\".\"c\";"), string.Join("\n", statements));
        Assert.IsLessThan(alter, statements.IndexOf("DROP TRIGGER \"tr\" ON \"s\".\"c\";"), string.Join("\n", statements));
        Assert.IsGreaterThan(alter, statements.FindIndex(static statement => statement.StartsWith("CREATE POLICY \"p\"", StringComparison.Ordinal)));
        Assert.IsGreaterThan(alter, statements.FindIndex(static statement => statement.StartsWith("CREATE TRIGGER tr ", StringComparison.Ordinal)));
        Assert.IsFalse(statements.Any(static statement => statement.Contains("\"other\"", StringComparison.Ordinal) || statement.StartsWith("CREATE TRIGGER other", StringComparison.Ordinal)),
            "A trigger that doesn't use the column is left alone.");
    }

    [TestMethod]
    public void AGeneratedColumnInTheWayIsRefused()
    {
        const string Before = "CREATE SCHEMA s; CREATE TABLE s.t (id int PRIMARY KEY, a int, g int GENERATED ALWAYS AS (a * 2) STORED);";
        var live = Build(Before);
        live.LiveDependencies = [new LiveDependency("generated column", "s.t.g", "s.t", "a")];

        var blocked = Batch(live, Build(Before.Replace("a int,", "a bigint,", StringComparison.Ordinal))).GetUnsupportedOperations().Single();

        StringAssert.Contains(blocked.GenerateSql(), "generated column s.t.g");
        StringAssert.Contains(blocked.GenerateSql(), "Remove the generated column in one publish");
    }

    [TestMethod]
    public void AGeneratedColumnTheSameAlterDropsIsNotInTheWay()
    {
        var live = Build("CREATE SCHEMA s; CREATE TABLE s.t (id int PRIMARY KEY, a int, g int GENERATED ALWAYS AS (a * 2) STORED);");
        live.LiveDependencies = [new LiveDependency("generated column", "s.t.g", "s.t", "a")];

        var batch = Batch(live, Build("CREATE SCHEMA s; CREATE TABLE s.t (id int PRIMARY KEY, a bigint);"));

        Assert.IsEmpty(batch.GetUnsupportedOperations());
    }

    [TestMethod]
    public void TheColumnsInTheWayAreThoseDroppedRetypedOrRegenerated()
    {
        var before = Build("CREATE TABLE t (a int, b int, c int, d int, g int GENERATED ALWAYS AS (a + 1) STORED, h int GENERATED ALWAYS AS (a + 2) STORED);").Tables["public.t"];
        var after = Build("CREATE TABLE t (a int, b bigint, c int NOT NULL, g int GENERATED ALWAYS AS (a + 10) STORED, h int GENERATED ALWAYS AS (a + 2) STORED);").Tables["public.t"];

        CollectionAssert.AreEquivalent(new[] { "b", "d", "g" }, ScriptGenerator.ColumnsChangedUnderDependents(before, after).ToArray());
    }

    private static DatabaseModel Live(string sql)
    {
        var model = Build(sql);
        model.LiveDependencies = Dependencies;
        return model;
    }

    private static OperationBatch Batch(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

    private static IReadOnlyList<SqlOperation> Operations(DatabaseModel oldModel, DatabaseModel newModel) => Batch(oldModel, newModel).Operations;

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel)
        => Operations(oldModel, newModel)
            .Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .ToList();

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
