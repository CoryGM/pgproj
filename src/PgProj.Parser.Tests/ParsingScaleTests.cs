using System.Text;

namespace PgProj.Parser.Tests;

/// <summary>
/// G-321: the parser's two fixed quadratics stay fixed, timed as <c>copilot-tests.md</c> §3 allows: input eight times larger must take well under the
/// sixty-four times a quadratic takes (linear is about eight). Before G-310 a 2,000-column table took 39 times as long as a 250-column one; before G-315 a
/// file of 20,000 warnings took 51 times as long as one of 2,500. The counters of <c>StatementTextPassTests</c> and <c>TextPositionTests</c> guard the
/// same fixes deterministically. G-327 doubled the sizes (a 250-column table parses in a few milliseconds, where a busy machine's pauses weigh most) and
/// times the fastest run, after the median failed at 24 on a small CI runner.
/// </summary>
[TestClass]
[TestCategory("Performance")]
[DoNotParallelize]
public sealed class ParsingScaleTests
{
    private const double Bound = 20;

    [TestMethod]
    public void AWideTableParsesInTimeLinearInItsColumns()
    {
        // G-310: every expression's text was read by lexing the rest of the statement. One column per line, as pg_dump and init write a table.
        var parser = new PgQueryParser();
        var narrow = Table(500);
        var wide = Table(4_000);

        var ratio = TimedRuns.Ratio(() => parser.Parse(narrow), () => parser.Parse(wide));

        Assert.IsLessThan(Bound, ratio, $"8x the columns took {ratio:F1}x the time");
    }

    [TestMethod]
    public void AWideTableWrittenOnOneLineParsesInTimeLinearInItsColumns()
    {
        // G-322: for every column, the rename-annotation lookup copied the part of its line before it, and its position was counted from the start
        // of its line, each of which on one line is everything before it.
        var parser = new PgQueryParser();
        var narrow = Table(500).Replace("\n", " ", StringComparison.Ordinal);
        var wide = Table(4_000).Replace("\n", " ", StringComparison.Ordinal);

        var ratio = TimedRuns.Ratio(() => parser.Parse(narrow), () => parser.Parse(wide));

        Assert.IsLessThan(Bound, ratio, $"8x the columns took {ratio:F1}x the time");
    }

    [TestMethod]
    public void AFileOfManyWarningsIsPositionedInTimeLinearInItsLength()
    {
        // G-315: every diagnostic's line was counted from the start of the file.
        var parser = new PgQueryParser();
        var small = Inserts(5_000);
        var large = Inserts(40_000);

        var ratio = TimedRuns.Ratio(() => parser.Parse(small), () => parser.Parse(large));

        Assert.IsLessThan(Bound, ratio, $"8x the warnings took {ratio:F1}x the time");
    }

    private static string Table(int columns)
        => $"CREATE TABLE public.wide (\n    {string.Join(",\n    ", Enumerable.Range(0, columns).Select(static i => $"c{i} integer DEFAULT 0 NOT NULL"))}\n);";

    private static string Inserts(int statements)
    {
        var sql = new StringBuilder();
        for (var i = 0; i < statements; i++)
        {
            sql.Append($"INSERT INTO t VALUES ({i});\n");
        }

        return sql.ToString();
    }
}
