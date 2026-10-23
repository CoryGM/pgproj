using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-215: a materialized view's <c>WITH (...)</c> storage parameters and <c>WITH NO DATA</c> are deployed as written. The parameters are compared as a set
/// and changed in place; <c>WITH NO DATA</c> is how it is created, not compared.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class MaterializedViewStorageTests
{
    private const string Table = "CREATE TABLE public.t (a int);";

    [TestMethod]
    public void TheOptionsAndWithNoDataAreRead()
    {
        var view = new PgQueryParser().Parse("CREATE MATERIALIZED VIEW public.mv WITH (fillfactor = 50) AS SELECT 1 AS a WITH NO DATA;").Nodes.OfType<ViewNode>().Single();

        Assert.AreEqual("fillfactor = 50", view.StorageOptions.Single().RawText.Trim());
        Assert.IsTrue(view.WithNoData);
        Assert.IsFalse(view.RawBodyExpression!.Contains("DATA", StringComparison.OrdinalIgnoreCase), view.RawBodyExpression);
    }

    [TestMethod]
    public void TheyAreCreatedAsWritten()
    {
        var script = ScriptFor(DatabaseModel.Empty, Build($"{Table} CREATE MATERIALIZED VIEW public.mv WITH (fillfactor = 50) AS SELECT a FROM public.t WITH NO DATA;"));

        StringAssert.Contains(script, "CREATE MATERIALIZED VIEW \"public\".\"mv\" WITH (fillfactor = 50) AS\nSELECT a FROM public.t\nWITH NO DATA;", script);
    }

    [TestMethod]
    public void WithDataIsNotWritten()
    {
        var script = ScriptFor(DatabaseModel.Empty, Build($"{Table} CREATE MATERIALIZED VIEW public.mv AS SELECT a FROM public.t WITH DATA;"));

        Assert.IsFalse(script.Contains("NO DATA", StringComparison.Ordinal), script);
    }

    [TestMethod]
    public void ChangedOptionsAreSetAndRemovedOnesResetInPlace()
    {
        var script = ScriptFor(
            Build($"{Table} CREATE MATERIALIZED VIEW public.mv WITH (fillfactor = 50, autovacuum_enabled = false) AS SELECT a FROM public.t;"),
            Build($"{Table} CREATE MATERIALIZED VIEW public.mv WITH (fillfactor = 70) AS SELECT a FROM public.t;"));

        StringAssert.Contains(script, "ALTER MATERIALIZED VIEW \"public\".\"mv\" SET (fillfactor = 70);", script);
        StringAssert.Contains(script, "ALTER MATERIALIZED VIEW \"public\".\"mv\" RESET (autovacuum_enabled);", script);
        Assert.IsFalse(script.Contains("DROP MATERIALIZED VIEW", StringComparison.Ordinal), script);
    }

    [TestMethod]
    [DataRow("WITH (fillfactor=50, autovacuum_enabled=false)", "WITH (autovacuum_enabled = false, fillfactor = 50)")]
    [DataRow("", "")]
    public void TheSameOptionsInAnotherOrderOrSpellingAreNoChange(string before, string after)
    {
        var changes = new DiffEngine().Diff(
            Build($"{Table} CREATE MATERIALIZED VIEW public.mv {before} AS SELECT a FROM public.t;"),
            Build($"{Table} CREATE MATERIALIZED VIEW public.mv {after} AS SELECT a FROM public.t WITH NO DATA;"));

        Assert.IsEmpty(changes.Modified, string.Join("; ", changes.Modified.SelectMany(static change => change.Details.Select(static detail => detail.PropertyName))));
    }

    private static string ScriptFor(DatabaseModel oldModel, DatabaseModel newModel)
        => new ScriptGenerator().GenerateSqlScript(new ScriptGenerator().Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel)));

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
