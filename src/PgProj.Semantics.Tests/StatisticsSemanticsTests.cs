using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>G-150: extended statistics are a model object keyed by schema and name, about one table, with a comparison form for the kinds and the expressions.</summary>
[TestClass]
public sealed class StatisticsSemanticsTests
{
    private const string Table = "CREATE TABLE app.t (a int, b int, c text); ";

    [TestMethod]
    public void StatisticsAreInTheModelAndNameTheirTable()
    {
        var model = Build("CREATE SCHEMA app; " + Table + "CREATE STATISTICS app.t_ab (dependencies) ON a, b FROM app.t;");

        var statistics = model.Statistics["statistics:app.t_ab"];
        Assert.AreEqual("t_ab", statistics.Name);
        Assert.AreEqual("app", statistics.SchemaName);
        CollectionAssert.AreEqual(new[] { "app.t" }, statistics.ReferencedTables.ToArray());
        Assert.AreEqual(0, model.Diagnostics.Count, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void TheKindsHaveOneComparisonFormWhateverTheirOrderOrWhetherAllThreeAreWritten()
    {
        var all = Build("CREATE SCHEMA app; " + Table + "CREATE STATISTICS app.s ON a, b FROM app.t;").Statistics.Values.Single();
        var spelled = Build("CREATE SCHEMA app; " + Table + "CREATE STATISTICS app.s (mcv, ndistinct, dependencies) ON a, b FROM app.t;").Statistics.Values.Single();
        var one = Build("CREATE SCHEMA app; " + Table + "CREATE STATISTICS app.s (mcv) ON a, b FROM app.t;").Statistics.Values.Single();

        CollectionAssert.AreEqual(all.NormalizedKinds.ToArray(), spelled.NormalizedKinds.ToArray());
        CollectionAssert.AreEqual(new[] { "mcv" }, one.NormalizedKinds.ToArray());
    }

    [TestMethod]
    public void AnExpressionIsCanonicalizedSoItsSpellingsMatch()
    {
        var first = Build("CREATE SCHEMA app; " + Table + "CREATE STATISTICS app.s ON (lower(c)), a FROM app.t;").Statistics.Values.Single();
        var second = Build("CREATE SCHEMA app; " + Table + "CREATE STATISTICS app.s ON (LOWER((c))), a FROM app.t;").Statistics.Values.Single();

        Assert.IsNotNull(first.Elements[0].NormalizedExpression);
        Assert.AreEqual(first.Elements[0].NormalizedExpression, second.Elements[0].NormalizedExpression);
    }

    [TestMethod]
    public void ADuplicateNameIsAnError()
    {
        var model = Build("CREATE SCHEMA app; " + Table + "CREATE STATISTICS app.s ON a, b FROM app.t; CREATE STATISTICS app.s ON a, c FROM app.t;");

        Assert.IsTrue(model.HasErrors);
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
