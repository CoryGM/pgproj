using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-229: a column in an index's expression or predicate was bound to a column of the same name on another table (a partition, or any table), and build refused
/// the valid index ("binds column ... that does not belong to table"). An unqualified name in an index is now its own table's column.
/// </summary>
[TestClass]
public sealed class IndexColumnBindingTests
{
    [TestMethod]
    public void AnUnqualifiedColumnInAPredicateOrExpressionIsTheIndexsTables()
    {
        var model = Build("""
            CREATE TABLE public.other (d int, f1 int);
            CREATE TABLE public.t (b int, d int, f1 int) PARTITION BY RANGE (b);
            CREATE TABLE public.t_1 PARTITION OF public.t FOR VALUES FROM (1) TO (10);
            CREATE INDEX t_d_pred ON ONLY public.t USING btree (d) WHERE (d = 2);
            CREATE INDEX t_expr ON public.t ((d + f1));
            CREATE TABLE public.zz (f1 int);
            CREATE INDEX zz_i ON public.zz ((f1 + 1)) WHERE f1 > 0;
            """);

        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("binds column", StringComparison.Ordinal)), string.Join("\n", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        CollectionAssert.AreEquivalent(new[] { "public.t.d" }, model.Indexes["public.t_d_pred"].ResolvedColumns.Select(static column => column.Key).ToArray());
        CollectionAssert.AreEquivalent(new[] { "public.t.d", "public.t.f1" }, model.Indexes["public.t_expr"].ResolvedColumns.Select(static column => column.Key).ToArray());
        CollectionAssert.AreEquivalent(new[] { "public.zz.f1" }, model.Indexes["public.zz_i"].ResolvedColumns.Select(static column => column.Key).ToArray());
    }

    [TestMethod]
    public void ACheckConstraintsColumnIsItsTablesToo()
    {
        var model = Build("CREATE TABLE public.a (x int); CREATE TABLE public.b (x int, CONSTRAINT b_x CHECK (x > 0));");

        CollectionAssert.AreEquivalent(new[] { "public.b.x" }, model.Constraints["public.b.b_x"].ResolvedColumns.Select(static column => column.Key).ToArray());
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
