using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-138: what uses a project's operator is created after it and dropped before it, and when the operator has to be dropped and created again (an operator
/// cannot be altered) whatever the live database records as using it is taken off first and put back after: a column default and a domain CHECK are
/// detached and reattached, and a view, index, or CHECK is dropped and recreated.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class OperatorDependentDeployTests
{
    private const string Head = "CREATE SCHEMA s; CREATE FUNCTION s.same(a int, b int) RETURNS boolean LANGUAGE sql IMMUTABLE AS $$ SELECT a = b $$; ";

    private const string Users = """

        CREATE TABLE s.c (id int PRIMARY KEY, code int NOT NULL DEFAULT (CASE WHEN 1 OPERATOR(s.===) 1 THEN 1 ELSE 0 END), n int, CONSTRAINT c_ck CHECK (n OPERATOR(s.===) n));
        CREATE INDEX c_idx ON s.c (n) WHERE n OPERATOR(s.===) 1;
        CREATE VIEW s.v AS SELECT id, (n OPERATOR(s.===) 1) AS one FROM s.c;
        CREATE DOMAIN s.pos AS int CHECK (VALUE OPERATOR(s.===) VALUE);
        """;

    private const string Before = Head + "CREATE OPERATOR s.=== (LEFTARG = int, RIGHTARG = int, FUNCTION = s.same);" + Users;
    private const string After = Head + "CREATE OPERATOR s.=== (LEFTARG = int, RIGHTARG = int, FUNCTION = s.same, HASHES);" + Users;

    private static readonly string Key = OperatorNames.CreateKey("s", "===", "integer", "integer");

    private static readonly LiveDependency[] Uses =
    [
        new("default", "s.c.code", Key, null, "operator"),
        new("check", "s.c.c_ck", Key, null, "operator"),
        new("index", "s.c_idx", Key, null, "operator"),
        new("view", "s.v", Key, null, "operator"),
        new("domain check", "s.pos.pos_check", Key, null, "operator"),
    ];

    [TestMethod]
    public void EverythingThatUsesAnOperatorIsCreatedAfterIt()
    {
        var statements = Statements(DatabaseModel.Empty, Build(Before));

        var operatorAt = statements.FindIndex(static s => s.StartsWith("CREATE OPERATOR", StringComparison.Ordinal));
        Assert.IsGreaterThanOrEqualTo(0, operatorAt, string.Join("\n---\n", statements));
        foreach (var prefix in new[] { "CREATE TABLE \"s\".\"c\"", "CREATE OR REPLACE VIEW \"s\".\"v\"", "CREATE INDEX \"c_idx\"", "CREATE DOMAIN \"s\".\"pos\"" })
        {
            var at = statements.FindIndex(s => s.StartsWith(prefix, StringComparison.Ordinal));
            Assert.IsGreaterThan(operatorAt, at, prefix + "\n" + string.Join("\n---\n", statements));
        }
    }

    [TestMethod]
    public void AFunctionBodyThatUsesAnOperatorIsCreatedAfterIt()
    {
        var statements = Statements(
            DatabaseModel.Empty,
            Build(Head + "CREATE FUNCTION s.uses(x int) RETURNS boolean LANGUAGE sql BEGIN ATOMIC SELECT x OPERATOR(s.===) 1; END; CREATE OPERATOR s.=== (LEFTARG = int, RIGHTARG = int, FUNCTION = s.same);"));

        var operatorAt = statements.FindIndex(static s => s.StartsWith("CREATE OPERATOR", StringComparison.Ordinal));
        var usesAt = statements.FindIndex(static s => s.StartsWith("CREATE OR REPLACE FUNCTION \"s\".\"uses\"", StringComparison.Ordinal));
        Assert.IsTrue(operatorAt >= 0 && usesAt > operatorAt, string.Join("\n---\n", statements));
    }

    [TestMethod]
    public void ARemovedOperatorIsDroppedAfterWhatUsedIt()
    {
        var withoutOperator = Build(Head + Users.Replace("OPERATOR(s.===)", "=", StringComparison.Ordinal));
        var statements = Statements(Build(Before), withoutOperator);

        var operatorAt = statements.FindIndex(static s => s.StartsWith("DROP OPERATOR", StringComparison.Ordinal));
        Assert.IsGreaterThanOrEqualTo(0, operatorAt, string.Join("\n---\n", statements));
    }

    [TestMethod]
    public void EverythingUsingTheOperatorIsOutOfTheWayBeforeItsDropAndBackAfterItsCreate()
    {
        var statements = Statements(Live(Before, Uses), Build(After));

        var drop = statements.FindIndex(static s => s.StartsWith("DROP OPERATOR \"s\".===", StringComparison.Ordinal));
        var create = statements.FindIndex(static s => s.StartsWith("CREATE OPERATOR \"s\".===", StringComparison.Ordinal));
        var order = string.Join("\n", statements);
        Assert.IsTrue(drop >= 0 && create > drop, order);

        foreach (var detach in new[]
        {
            "ALTER TABLE \"s\".\"c\" ALTER COLUMN \"code\" DROP DEFAULT;",
            "ALTER DOMAIN \"s\".\"pos\" DROP CONSTRAINT \"pos_check\";",
            "ALTER TABLE \"s\".\"c\" DROP CONSTRAINT \"c_ck\";",
            "DROP INDEX \"s\".\"c_idx\";",
            "DROP VIEW \"s\".\"v\";",
        })
        {
            var index = statements.IndexOf(detach);
            Assert.IsTrue(index >= 0 && index < drop, detach + "\n" + order);
        }

        foreach (var reattach in new[]
        {
            "ALTER TABLE \"s\".\"c\" ALTER COLUMN \"code\" SET DEFAULT (CASE WHEN 1 OPERATOR(s.===) 1 THEN 1 ELSE 0 END);",
            "ALTER DOMAIN \"s\".\"pos\" VALIDATE CONSTRAINT \"pos_check\";",
            "ALTER TABLE \"s\".\"c\" VALIDATE CONSTRAINT \"c_ck\";",
        })
        {
            Assert.IsGreaterThan(create, statements.IndexOf(reattach), reattach + "\n" + order);
        }

        Assert.IsGreaterThan(create, statements.FindIndex(static s => s.StartsWith("CREATE INDEX \"c_idx\"", StringComparison.Ordinal)), order);
        Assert.IsGreaterThan(create, statements.FindIndex(static s => s.StartsWith("CREATE OR REPLACE VIEW \"s\".\"v\"", StringComparison.Ordinal)), order);
    }

    [TestMethod]
    public void ARebuildedOperatorIsNotDestructive()
    {
        var batch = Batch(Live(Before, Uses), Build(After));

        Assert.IsEmpty(batch.GetDestructiveOperations());
        Assert.IsFalse(batch.Operations.OfType<BlockedOperation>().Any());
    }

    [TestMethod]
    public void AnOperatorThatDoesNotChangeDetachesNothing()
    {
        var statements = Statements(Live(Before, Uses), Build(Before));

        Assert.IsEmpty(statements, string.Join("\n---\n", statements));
    }

    [TestMethod]
    public void AnObjectTheProjectDoesNotHaveThatUsesTheOperatorIsRefused()
    {
        var batch = Batch(Live(Before, [.. Uses, new LiveDependency("view", "other.v", Key, null, "operator")]), Build(After));

        var blocked = batch.Operations.OfType<BlockedOperation>().Single();
        StringAssert.Contains(blocked.Message, "other.v");
        StringAssert.Contains(blocked.Message, "operator");
    }

    [TestMethod]
    public void AnOperatorWhoseFunctionIsDroppedAndCreatedAgainIsRebuiltToo()
    {
        // The function's return type changes (a drop and create), and the operator that runs it is recorded as using it.
        var before = Head + "CREATE OPERATOR s.=== (LEFTARG = int, RIGHTARG = int, FUNCTION = s.same);";
        var after = "CREATE SCHEMA s; CREATE FUNCTION s.same(a int, b int) RETURNS int LANGUAGE sql IMMUTABLE AS $$ SELECT a $$; CREATE OPERATOR s.=== (LEFTARG = int, RIGHTARG = int, FUNCTION = s.same);";
        var live = Live(before, [new LiveDependency("operator", Key, "s.same(int4,int4)", null, "routine")]);

        var statements = Statements(live, Build(after));

        var dropOperator = statements.FindIndex(static s => s.StartsWith("DROP OPERATOR", StringComparison.Ordinal));
        var dropFunction = statements.FindIndex(static s => s.StartsWith("DROP FUNCTION", StringComparison.Ordinal));
        var createOperator = statements.FindIndex(static s => s.StartsWith("CREATE OPERATOR", StringComparison.Ordinal));
        var createFunction = statements.FindIndex(static s => s.StartsWith("CREATE OR REPLACE FUNCTION", StringComparison.Ordinal));
        Assert.IsTrue(dropOperator >= 0 && dropFunction > dropOperator && createFunction > dropFunction && createOperator > createFunction, string.Join("\n---\n", statements));
    }

    private static DatabaseModel Live(string sql, IReadOnlyList<LiveDependency> dependencies)
    {
        var model = Build(sql);
        model.LiveDependencies = dependencies;
        return model;
    }

    private static OperationBatch Batch(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel)
        => Batch(oldModel, newModel).Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .ToList();

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
