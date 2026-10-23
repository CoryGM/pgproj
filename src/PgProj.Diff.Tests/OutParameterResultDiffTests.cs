using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-187: a function declared with OUT parameters and no RETURNS returns their type (one output) or <c>record</c> (several), and a live database prints that
/// RETURNS. The project's has none, so the function was modified after every publish. The two are one function; a changed output is a difference.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class OutParameterResultDiffTests
{
    private const string Body = "LANGUAGE sql AS $$ SELECT 1, 'a' $$";

    [TestMethod]
    [DataRow("g(OUT x int, OUT y text)", "g(OUT x int, OUT y text) RETURNS record")]
    [DataRow("g(a int, OUT x int, OUT y text)", "g(a int, OUT x integer, OUT y text) RETURNS record")]
    [DataRow("g(INOUT x int, OUT y text)", "g(INOUT x int, OUT y text) RETURNS record")]
    public void SeveralOutputsAreTheSameFunctionAsOneThatStatesRecord(string written, string stored)
    {
        var project = Build($"CREATE FUNCTION {written} {Body};");
        var live = Build($"CREATE FUNCTION {stored} {Body};");

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(live, project)));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(project, live)));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(project, project)));
    }

    [TestMethod]
    public void OneOutputIsTheSameFunctionAsOneThatStatesItsType()
    {
        var project = Build("CREATE FUNCTION g(OUT x int) LANGUAGE sql AS $$ SELECT 1 $$;");
        var live = Build("CREATE FUNCTION g(OUT x int) RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;");

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(live, project)));
    }

    [TestMethod]
    [DataRow("g(OUT x int, OUT y text)", "g(OUT x int, OUT y date)")]
    [DataRow("g(OUT x int, OUT y text)", "g(OUT x int)")]
    [DataRow("g(OUT x int)", "g(OUT x bigint)")]
    public void AChangedOutputIsADifference(string before, string after)
    {
        Assert.IsFalse(IsEmpty(new DiffEngine().Diff(Build($"CREATE FUNCTION {before} {Body};"), Build($"CREATE FUNCTION {after} {Body};"))));
    }

    private static bool IsEmpty(ChangeSet diff) => diff.Added.Count + diff.Removed.Count + diff.Modified.Count == 0;

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
