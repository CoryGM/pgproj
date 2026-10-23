using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-105: a routine whose return type or arguments change is dropped and created again, and PostgreSQL won't drop it while anything
/// uses it. What the live database records as using it is taken off first and put back after: a column default and a domain CHECK are
/// detached and reattached, and a view, index, CHECK, trigger, or policy is dropped and recreated (a CHECK re-added <c>NOT VALID</c> and
/// then validated). Something pgproj can't recreate is refused.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RoutineDependentDropTests
{
    private const string Users = """

        CREATE TABLE s.c (id int PRIMARY KEY, code int NOT NULL DEFAULT s.next_code(1), n int, CONSTRAINT c_code_check CHECK (s.next_code(n) >= 0));
        CREATE INDEX c_code_idx ON s.c ((s.next_code(n)));
        CREATE VIEW s.v AS SELECT id, s.next_code(n) AS nc FROM s.c;
        ALTER TABLE s.c ENABLE ROW LEVEL SECURITY;
        CREATE POLICY p ON s.c USING (s.next_code(n) >= 0);
        CREATE DOMAIN s.pos AS int CHECK (s.next_code(VALUE) >= 0);
        """;

    private const string Before = "CREATE SCHEMA s; CREATE FUNCTION s.next_code(x int) RETURNS int LANGUAGE sql IMMUTABLE AS $$ SELECT x $$;" + Users;
    private const string After = "CREATE SCHEMA s; CREATE FUNCTION s.next_code(x int) RETURNS bigint LANGUAGE sql IMMUTABLE AS $$ SELECT x::bigint $$;" + Users;

    private static readonly LiveDependency[] Uses =
    [
        new("default", "s.c.code", "s.next_code(int4)", null, "routine"),
        new("check", "s.c.c_code_check", "s.next_code(int4)", null, "routine"),
        new("index", "s.c_code_idx", "s.next_code(int4)", null, "routine"),
        new("view", "s.v", "s.next_code(int4)", null, "routine"),
        new("policy", "s.c.p", "s.next_code(int4)", null, "routine"),
        new("domain check", "s.pos.pos_check", "s.next_code(int4)", null, "routine"),
    ];

    [TestMethod]
    public void EverythingUsingTheRoutineIsOutOfTheWayBeforeItsDropAndBackAfterItsCreate()
    {
        var statements = Statements(Live(Before, Uses), Build(After));

        var drop = statements.FindIndex(static statement => statement.StartsWith("DROP FUNCTION \"s\".\"next_code\"", StringComparison.Ordinal));
        var create = statements.FindIndex(static statement => statement.StartsWith("CREATE OR REPLACE FUNCTION \"s\".\"next_code\"", StringComparison.Ordinal));
        var order = string.Join("\n", statements);
        foreach (var detach in new[]
        {
            "ALTER TABLE \"s\".\"c\" ALTER COLUMN \"code\" DROP DEFAULT;",
            "ALTER DOMAIN \"s\".\"pos\" DROP CONSTRAINT \"pos_check\";",
            "ALTER TABLE \"s\".\"c\" DROP CONSTRAINT \"c_code_check\";",
            "DROP INDEX \"s\".\"c_code_idx\";",
            "DROP VIEW \"s\".\"v\";",
            "DROP POLICY \"p\" ON \"s\".\"c\";",
        })
        {
            var index = statements.IndexOf(detach);
            Assert.IsTrue(index >= 0 && index < drop, detach + "\n" + order);
        }

        foreach (var reattach in new[]
        {
            "ALTER TABLE \"s\".\"c\" ALTER COLUMN \"code\" SET DEFAULT s.next_code(1);",
            "ALTER DOMAIN \"s\".\"pos\" VALIDATE CONSTRAINT \"pos_check\";",
            "ALTER TABLE \"s\".\"c\" VALIDATE CONSTRAINT \"c_code_check\";",
            "CREATE INDEX \"c_code_idx\" ON \"s\".\"c\" USING btree ((s.next_code(n)));",
            "CREATE POLICY \"p\" ON \"s\".\"c\" USING (s.next_code(n) >= 0);",
        })
        {
            Assert.IsGreaterThan(create, statements.IndexOf(reattach), reattach + "\n" + order);
        }

        Assert.IsTrue(statements.Any(static statement => statement.EndsWith("CHECK (s.next_code(n) >= 0) NOT VALID;", StringComparison.Ordinal)), order);
        Assert.IsTrue(statements.Any(static statement => statement.EndsWith("CHECK (s.next_code(VALUE) >= 0) NOT VALID;", StringComparison.Ordinal)), order);
    }

    [TestMethod]
    public void ABodyOnlyChangeIsReplacedInPlaceAndDetachesNothing()
    {
        var statements = Statements(Live(Before, Uses), Build(Before.Replace("SELECT x $$", "SELECT x + 0 $$", StringComparison.Ordinal)));

        Assert.IsFalse(statements.Any(static statement => statement.StartsWith("DROP", StringComparison.Ordinal) || statement.Contains("DROP DEFAULT", StringComparison.Ordinal)),
            string.Join("\n", statements));
    }

    [TestMethod]
    public void SomethingPgprojDoesNotModelIsRefused()
    {
        var blocked = Batch(Live(Before, [new("other", "cast from integer to s.money", "s.next_code(int4)", null, "routine")]), Build(After))
            .GetUnsupportedOperations().Single();

        StringAssert.Contains(blocked.GenerateSql(), "cast from integer to s.money");
        StringAssert.Contains(blocked.GenerateSql(), "not something pgproj models");
    }

    [TestMethod]
    public void AUserTheProjectDoesNotHaveIsRefused()
    {
        var blocked = Batch(Live(Before, [new("view", "other.report", "s.next_code(int4)", null, "routine")]), Build(After))
            .GetUnsupportedOperations().Single();

        StringAssert.Contains(blocked.GenerateSql(), "other.report");
        StringAssert.Contains(blocked.GenerateSql(), "not in this project");
    }

    [TestMethod]
    public void TheRoutineKeepsAnotherRolesOwnership()
    {
        var live = Live(Before, Uses);
        live.LiveOwners = new Dictionary<string, string> { ["s.next_code(int4)"] = "app_owner" };
        live.ConnectedRole = "deployer";

        CollectionAssert.Contains(Statements(live, Build(After)), "ALTER FUNCTION \"s\".\"next_code\"(int) OWNER TO \"app_owner\";");
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
            .Select(static operation => operation.GenerateSql().Trim().Replace("\r\n", "\n", StringComparison.Ordinal))
            .ToList();

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
