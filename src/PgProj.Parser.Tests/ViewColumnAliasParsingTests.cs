namespace PgProj.Parser.Tests;

/// <summary>G-189: the column name list of <c>CREATE VIEW v (a, b) AS ...</c> is read, for a view and a materialized view.</summary>
[TestClass]
public sealed class ViewColumnAliasParsingTests
{
    [TestMethod]
    public void TheColumnListOfAViewIsRead()
    {
        var tree = new PgQueryParser().Parse("CREATE VIEW public.v (x, \"Y z\") WITH (security_barrier) AS SELECT id, name FROM public.e;");

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var view = tree.Views.Single();
        CollectionAssert.AreEqual(new[] { "x", "Y z" }, view.ColumnAliases.ToArray());
        Assert.AreEqual("SELECT id, name FROM public.e", view.RawBodyExpression);
    }

    [TestMethod]
    public void TheColumnListOfAMaterializedViewIsRead()
    {
        var tree = new PgQueryParser().Parse("CREATE MATERIALIZED VIEW public.m (x, y) AS SELECT id, name FROM public.e WITH NO DATA;");

        CollectionAssert.AreEqual(new[] { "x", "y" }, tree.Views.Single().ColumnAliases.ToArray());
    }

    [TestMethod]
    public void AViewWithoutAColumnListHasNone_AndARecursiveViewKeepsItsListInItsBody()
    {
        var plain = new PgQueryParser().Parse("CREATE VIEW public.v AS SELECT id FROM public.e;");
        var recursive = new PgQueryParser().Parse("CREATE RECURSIVE VIEW public.r (n) AS VALUES (1) UNION ALL SELECT n + 1 FROM r WHERE n < 3;");

        Assert.IsEmpty(plain.Views.Single().ColumnAliases);
        Assert.IsEmpty(recursive.Views.Single().ColumnAliases);
        StringAssert.Contains(recursive.Views.Single().RawBodyExpression, "WITH RECURSIVE r (n)");
    }
}
