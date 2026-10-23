using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-217: a view's <c>WITH (...)</c> options are deployed as written, one set to its default value included (<c>security_barrier = false</c> was dropped);
/// the comparison still treats such an option as none.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ViewOptionsAsWrittenTests
{
    private const string Table = "CREATE TABLE public.t (a int);";

    [TestMethod]
    [DataRow("security_barrier = false")]
    [DataRow("security_barrier=TRUE, security_invoker = on")]
    public void TheOptionsAreDeployedAsWritten(string options)
    {
        var script = ScriptFor(DatabaseModel.Empty, Build($"{Table} CREATE VIEW public.v WITH ({options}) AS SELECT a FROM public.t;"));

        StringAssert.Contains(script, $"CREATE OR REPLACE VIEW \"public\".\"v\" WITH ({options}) AS", script);
    }

    [TestMethod]
    public void AnOptionWrittenWithItsDefaultIsNoChangeFromNone()
    {
        var changes = new DiffEngine().Diff(
            Build($"{Table} CREATE VIEW public.v AS SELECT a FROM public.t;"),
            Build($"{Table} CREATE VIEW public.v WITH (security_barrier = false) AS SELECT a FROM public.t;"));

        Assert.IsEmpty(changes.Modified);
    }

    [TestMethod]
    public void AChangedOptionIsReplacedWithTheWrittenText()
    {
        var script = ScriptFor(
            Build($"{Table} CREATE VIEW public.v WITH (security_barrier = true) AS SELECT a FROM public.t;"),
            Build($"{Table} CREATE VIEW public.v WITH (security_barrier = false, security_invoker = true) AS SELECT a FROM public.t;"));

        StringAssert.Contains(script, "WITH (security_barrier = false, security_invoker = true) AS", script);
    }

    private static string ScriptFor(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().GenerateSqlScript(new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel)));

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
