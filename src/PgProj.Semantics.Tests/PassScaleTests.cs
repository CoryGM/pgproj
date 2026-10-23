using System.Text;
using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-321: the two passes G-314 made linear stay linear, timed as <c>copilot-tests.md</c> §3 allows: eight times the tables must take well under the
/// sixty-four times a quadratic takes (linear is about eight). Before G-314 each pass resolved a column reference by scanning every column of the model.
/// <c>ColumnKeyLookupTests</c> guards the same fix with a counter. G-330 times the fastest run and sets the bound at half a quadratic: the passes measure 13
/// to 17 times on eight times the tables (more than linear, never near 64), where the median failed at 21.6 against 20 on a busy CI runner; before G-314
/// they measured 88 to 106.
/// </summary>
[TestClass]
[TestCategory("Performance")]
[DoNotParallelize]
public sealed class PassScaleTests
{
    private const double Bound = 32;

    private static readonly Lazy<(SqlSyntaxTree[] Trees, DatabaseModel Model)> Small = new(() => Project(1_000));
    private static readonly Lazy<(SqlSyntaxTree[] Trees, DatabaseModel Model)> Large = new(() => Project(8_000));

    [TestMethod]
    public void ColumnDefaultsAreBoundInTimeLinearInTheProject()
    {
        var ratio = TimedRuns.Ratio(
            () => new DeepColumnDefaultPass().Execute(Small.Value.Model, Small.Value.Trees),
            () => new DeepColumnDefaultPass().Execute(Large.Value.Model, Large.Value.Trees));

        Assert.IsLessThan(Bound, ratio, $"8x the tables took {ratio:F1}x the time");
    }

    [TestMethod]
    public void IndexExpressionsAreBoundInTimeLinearInTheProject()
    {
        var ratio = TimedRuns.Ratio(
            () => new DeepIndexExpressionPass().Execute(Small.Value.Model, Small.Value.Trees),
            () => new DeepIndexExpressionPass().Execute(Large.Value.Model, Large.Value.Trees));

        Assert.IsLessThan(Bound, ratio, $"8x the tables took {ratio:F1}x the time");
    }

    /// <summary>Tables with a key, a foreign key to the one before, defaults, and an expression index, each naming its columns, and the model built from them.</summary>
    private static (SqlSyntaxTree[] Trees, DatabaseModel Model) Project(int tables)
    {
        var sql = new StringBuilder();
        for (var i = 0; i < tables; i++)
        {
            sql.Append($"CREATE TABLE public.t{i} (id bigint PRIMARY KEY, prev bigint REFERENCES public.t{Math.Max(i - 1, 0)} (id), name text NOT NULL DEFAULT '', created timestamptz DEFAULT now());\n");
            sql.Append($"CREATE INDEX ix_t{i}_name ON public.t{i} (lower(name));\n");
        }

        SqlSyntaxTree[] trees = [new PgQueryParser().Parse(sql.ToString())];
        return (trees, new SemanticPipeline().Execute(trees));
    }
}
