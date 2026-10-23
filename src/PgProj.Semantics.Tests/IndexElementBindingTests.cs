using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-90 part 3: an index's key columns, operator classes, and collations are bound from the parser's structured key elements, not by
/// finding the <c>CREATE INDEX</c> text with a regular expression. That expression took <c>IF</c> in <c>CREATE INDEX IF NOT EXISTS</c> for
/// the index's name and could not match an unnamed index at all, so neither had its key columns bound.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class IndexElementBindingTests
{
    private const string Table = "CREATE TABLE public.t (a int, b int, c int, d text);";

    [TestMethod]
    [DataRow("CREATE INDEX IF NOT EXISTS t_a_idx ON public.t (a);")]
    [DataRow("CREATE INDEX ON public.t (a);")]
    [DataRow("CREATE UNIQUE INDEX CONCURRENTLY t_a_idx ON public.t (a);")]
    [DataRow("CREATE INDEX t_a_idx ON public.t USING btree (a DESC NULLS LAST);")]
    public void AKeyColumnIsBoundHoweverTheIndexIsWritten(string index)
    {
        var model = Build(Table + index);

        CollectionAssert.Contains(model.Indexes.Values.Single().ReferencedColumns.ToList(), "public.t.a");
        AssertNoDiagnostics(model);
    }

    [TestMethod]
    public void EveryKeyColumnAndExpressionColumnIsBound()
    {
        var model = Build(Table + "CREATE INDEX t_multi_idx ON public.t (a, (b + c), d COLLATE \"C\" text_pattern_ops);");

        CollectionAssert.IsSubsetOf(new[] { "public.t.a", "public.t.b", "public.t.c", "public.t.d" }, model.Indexes.Values.Single().ReferencedColumns.ToArray());
        AssertNoDiagnostics(model);
    }

    [TestMethod]
    public void ABuiltInOperatorClassOrCollationIsNotAType()
    {
        var model = Build(Table + "CREATE INDEX t_d_idx ON public.t (d COLLATE \"C\" text_pattern_ops);");

        Assert.IsEmpty(model.Indexes.Values.Single().ReferencedTypes, string.Join(", ", model.Indexes.Values.Single().ReferencedTypes));
        AssertNoDiagnostics(model);
    }

    private static void AssertNoDiagnostics(DatabaseModel model)
        => Assert.IsEmpty(model.Diagnostics, string.Join(" | ", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
