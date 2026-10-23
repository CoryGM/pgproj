using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-64: a modified function or procedure was always dropped and recreated, and PostgreSQL rejects the drop (<c>2BP01</c>) when a
/// surviving object (a column default, a view, a constraint, an index) still uses it. When the parameters and return type are
/// unchanged it is now redeployed with <c>CREATE OR REPLACE</c> alone, which keeps the dependents and the routine's grants.
/// Anything PostgreSQL would refuse to replace, and anything whose own dependency is being dropped, is still dropped and recreated.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RoutineReplaceInPlaceTests
{
    private static string Function(int addend) => $"create function public.f(x integer) returns integer language sql as $$ select x + {addend} $$;";

    [TestMethod]
    public void ChangedBodyOfAFunctionUsedByATableDefaultIsReplacedNotDropped()
    {
        const string table = "create table public.t (n integer default public.f(1));";

        var statements = Statements(Build(Function(1) + table), Build(Function(2) + table));

        Assert.HasCount(1, statements, Dump(statements));
        StringAssert.StartsWith(statements[0], "CREATE OR REPLACE FUNCTION \"public\".\"f\"");
        StringAssert.Contains(statements[0], "x + 2");
    }

    [TestMethod]
    public void ChangedAttributesOfAFunctionAreAReplaceToo()
    {
        var statements = Changed(
            "create function public.f(x integer) returns integer language sql immutable as $$ select x $$;",
            "create function public.f(x integer) returns integer language sql volatile as $$ select x $$;");

        Assert.HasCount(1, statements, Dump(statements));
        StringAssert.StartsWith(statements[0], "CREATE OR REPLACE FUNCTION");
    }

    [TestMethod]
    public void ChangedBodyOfAProcedureIsReplacedNotDropped()
    {
        var statements = Changed(
            "create procedure public.p(x integer) language sql as $$ select 1 $$;",
            "create procedure public.p(x integer) language sql as $$ select 2 $$;");

        Assert.HasCount(1, statements, Dump(statements));
        StringAssert.StartsWith(statements[0], "CREATE OR REPLACE PROCEDURE");
    }

    [TestMethod]
    public void OverloadIsReplacedWithoutTouchingItsSibling()
    {
        var statements = Statements(
            Build("create function public.f(x integer) returns integer language sql as $$ select 1 $$; create function public.f(x text) returns integer language sql as $$ select 1 $$;"),
            Build("create function public.f(x integer) returns integer language sql as $$ select 2 $$; create function public.f(x text) returns integer language sql as $$ select 1 $$;"));

        Assert.HasCount(1, statements, Dump(statements));
        StringAssert.Contains(statements[0], "(x integer)");
    }

    [TestMethod]
    public void GrantsOnAReplacedFunctionAreNeitherRevokedNorGrantedAgain()
    {
        const string grant = "GRANT EXECUTE ON FUNCTION public.f(integer) TO r;";

        var statements = Statements(Build(Function(1) + grant), Build(Function(2) + grant));

        Assert.HasCount(1, statements, Dump(statements));
    }

    /// <summary>
    /// The old side of a publish is read from the live catalog, which spells types and defaults its own way; that is not a change
    /// to the signature, so a body edit is still a replace (found against a real server: the routine was dropped every time).
    /// </summary>
    [TestMethod]
    public void BodyChangeIsStillAReplaceWhenTheOldSideSpellsTypesAndDefaultsAsTheCatalogDoes()
    {
        var statements = Changed(
            "create function public.f(a integer, b text default 'x'::text, c character varying default null::character varying) returns setof integer language sql as $$ select 1 $$;",
            "create function public.f(a integer, b text default 'x', c varchar(10) default null) returns setof integer language sql as $$ select 2 $$;");

        Assert.HasCount(1, statements, Dump(statements));
        StringAssert.StartsWith(statements[0], "CREATE OR REPLACE FUNCTION");
    }

    // ---- what PostgreSQL cannot replace is still dropped and recreated ----

    [TestMethod]
    [DataRow("returns integer", "returns bigint", DisplayName = "return type")]
    [DataRow("returns integer", "returns setof integer", DisplayName = "returns a set")]
    public void ChangedReturnTypeIsDroppedThenCreated(string before, string after)
    {
        var statements = Changed(
            $"create function public.f(x integer) {before} language sql as $$ select 1 $$;",
            $"create function public.f(x integer) {after} language sql as $$ select 1 $$;");

        Assert.HasCount(2, statements, Dump(statements));
        StringAssert.StartsWith(statements[0], "DROP FUNCTION");
        StringAssert.StartsWith(statements[1], "CREATE OR REPLACE FUNCTION");
    }

    [TestMethod]
    public void RenamedParameterIsDroppedThenCreated()
    {
        var statements = Changed(
            "create function public.f(x integer) returns integer language sql as $$ select $1 $$;",
            "create function public.f(y integer) returns integer language sql as $$ select $1 $$;");

        Assert.HasCount(2, statements, Dump(statements));
        StringAssert.StartsWith(statements[0], "DROP FUNCTION");
    }

    [TestMethod]
    public void RemovedParameterDefaultIsDroppedThenCreated()
    {
        var statements = Changed(
            "create function public.f(x integer default 1) returns integer language sql as $$ select x $$;",
            "create function public.f(x integer) returns integer language sql as $$ select x $$;");

        Assert.HasCount(2, statements, Dump(statements));
        StringAssert.StartsWith(statements[0], "DROP FUNCTION");
    }

    [TestMethod]
    public void ChangedProcedureParameterModeIsDroppedThenCreated()
    {
        var statements = Changed(
            "create procedure public.p(x integer) language sql as $$ select 1 $$;",
            "create procedure public.p(inout x integer) language sql as $$ select 1 $$;");

        Assert.HasCount(2, statements, Dump(statements));
        StringAssert.StartsWith(statements[0], "DROP PROCEDURE");
    }

    /// <summary>The function uses a type that is dropped and recreated: it must be dropped before the type, or the type's drop is refused.</summary>
    [TestMethod]
    public void FunctionThatUsesATypeThatIsDroppedIsDroppedBeforeItToo()
    {
        var oldModel = Build("create type public.mood as enum ('a', 'b'); create function public.f(x public.mood) returns integer language sql as $$ select 1 $$;");
        var newModel = Build("create type public.mood as enum ('b', 'a'); create function public.f(x public.mood) returns integer language sql as $$ select 2 $$;");

        var statements = Statements(oldModel, newModel);

        var dropFunction = statements.FindIndex(static statement => statement.StartsWith("DROP FUNCTION", StringComparison.Ordinal));
        var dropType = statements.FindIndex(static statement => statement.StartsWith("DROP TYPE", StringComparison.Ordinal));
        Assert.IsGreaterThanOrEqualTo(0, dropFunction, Dump(statements));
        Assert.IsLessThan(dropType, dropFunction, Dump(statements));
    }

    /// <summary>The script for one routine changed from <paramref name="oldSql"/> to <paramref name="newSql"/>, through the real diff (G-66 made these changes visible to it).</summary>
    private static List<string> Changed(string oldSql, string newSql)
    {
        return Statements(Build(oldSql), Build(newSql));
    }

    private static string Dump(IEnumerable<string> statements) => string.Join(Environment.NewLine, statements);

    private static List<string> Statements(DatabaseModel oldModel, DatabaseModel newModel)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return batch.Operations
            .Where(static operation => operation is not SchemaCreateOperation)
            .Select(static operation => operation.GenerateSql().Trim())
            .ToList();
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
