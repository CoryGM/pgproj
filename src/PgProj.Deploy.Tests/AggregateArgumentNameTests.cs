using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-292: an aggregate's argument names (<c>VARIADIC items anycompatiblearray</c>) are deployed, so <c>pg_proc.proargnames</c>, <c>\df</c>, and the documentation keep them. They are
/// never part of the aggregate's identity or compared, as a routine's parameter names are not.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AggregateArgumentNameTests
{
    private const string Support = """
        CREATE FUNCTION public.acc(state int, v int) RETURNS int LANGUAGE sql AS 'SELECT state + v';
        CREATE FUNCTION public.acc_order(state int, v int, w int) RETURNS int LANGUAGE sql AS 'SELECT state + v + w';
        """;

    [TestMethod]
    public void ANamedArgumentIsDeployedWithItsName()
    {
        var script = Script(DatabaseModel.Empty, Build(Support + "CREATE AGGREGATE public.total(amount int) (SFUNC = public.acc, STYPE = int);"));

        StringAssert.Contains(script, "CREATE AGGREGATE \"public\".\"total\" (\"amount\" int)");
    }

    [TestMethod]
    public void AVariadicAndAnUnnamedArgumentKeepTheirForm()
    {
        var variadic = Script(DatabaseModel.Empty, Build(Support + "CREATE AGGREGATE public.total(VARIADIC amounts int[]) (SFUNC = public.acc, STYPE = int);"));
        var unnamed = Script(DatabaseModel.Empty, Build(Support + "CREATE AGGREGATE public.total2(int) (SFUNC = public.acc, STYPE = int);"));

        StringAssert.Contains(variadic, "(VARIADIC \"amounts\" int[])");
        StringAssert.Contains(unnamed, "\"total2\" (int)");
    }

    [TestMethod]
    public void AnOrderedSetAggregateNamesItsDirectAndOrderedArguments()
    {
        var script = Script(DatabaseModel.Empty, Build(Support + "CREATE AGGREGATE public.pct(frac int ORDER BY val int) (SFUNC = public.acc, STYPE = int);"));

        StringAssert.Contains(script, "(\"frac\" int ORDER BY \"val\" int)");
    }

    [TestMethod]
    public void TheNamesAreNotTheAggregatesIdentityAndAreNotCompared()
    {
        var named = Build(Support + "CREATE AGGREGATE public.total(amount int) (SFUNC = public.acc, STYPE = int);");
        var renamed = Build(Support + "CREATE AGGREGATE public.total(qty int) (SFUNC = public.acc, STYPE = int);");
        var unnamed = Build(Support + "CREATE AGGREGATE public.total(int) (SFUNC = public.acc, STYPE = int);");

        Assert.AreEqual(0, ChangeCount(new DiffEngine().Diff(named, renamed)));
        Assert.AreEqual(0, ChangeCount(new DiffEngine().Diff(named, unnamed)));
        Assert.AreEqual(0, ChangeCount(new DiffEngine().Diff(named, named)));
    }

    [TestMethod]
    public void DroppingAnAggregateNamesItByTypesAlone()
    {
        var script = Script(Build(Support + "CREATE AGGREGATE public.total(amount int) (SFUNC = public.acc, STYPE = int);"), Build(Support));

        StringAssert.Contains(script, "DROP AGGREGATE \"public\".\"total\" (int);");
    }

    private static int ChangeCount(ChangeSet diff) => diff.Added.Count + diff.Removed.Count + diff.Modified.Count;

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    private static string Script(DatabaseModel live, DatabaseModel project)
    {
        var batch = new ScriptGenerator().Generate(new DiffEngine().Diff(live, project), new DependencyAnalyzer().Analyze(live, project));
        return string.Join("\n", batch.Operations.Where(static operation => operation is not SchemaCreateOperation).Select(static operation => operation.GenerateSql().Replace("\r\n", "\n", StringComparison.Ordinal))).Trim();
    }
}
