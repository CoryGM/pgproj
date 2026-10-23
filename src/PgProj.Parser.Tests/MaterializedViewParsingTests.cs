namespace PgProj.Parser.Tests;

[TestClass]
public sealed class MaterializedViewParsingTests
{
    [TestMethod]
    public void CreateMaterializedViewProducesViewNodeWithIsMaterializedTrue()
    {
        var parser = new PgQueryParser();
        var tree = parser.Parse("CREATE MATERIALIZED VIEW public.product_summary AS SELECT id, name FROM public.products;");

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var view = tree.Views.Single(static node => node.IsMaterialized);
        Assert.AreEqual("product_summary", view.Name);
        Assert.AreEqual("public", view.SchemaName);
        Assert.IsTrue(view.IsMaterialized);
        Assert.AreEqual("SELECT id, name FROM public.products", view.RawBodyExpression);
    }

    [TestMethod]
    public void CreateRegularViewProducesViewNodeWithIsMaterializedFalse()
    {
        var parser = new PgQueryParser();
        var tree = parser.Parse("CREATE VIEW public.product_names AS SELECT name FROM public.products;");

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var view = tree.Views.Single();
        Assert.AreEqual("product_names", view.Name);
        Assert.IsFalse(view.IsMaterialized);
        Assert.AreEqual("SELECT name FROM public.products", view.RawBodyExpression);
    }

    [TestMethod]
    public void BothRegularAndMaterializedViewsAreParsedInSameSql()
    {
        var parser = new PgQueryParser();
        var tree = parser.Parse(
            """
            CREATE VIEW public.v_names AS SELECT name FROM public.products;
            CREATE MATERIALIZED VIEW public.mv_summary AS SELECT id, name FROM public.products;
            """);

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var views = tree.Views.ToList();
        Assert.HasCount(2, views);
        Assert.IsTrue(views.Any(static node => !node.IsMaterialized && node.Name == "v_names"));
        Assert.IsTrue(views.Any(static node => node.IsMaterialized && node.Name == "mv_summary"));
        Assert.AreEqual("SELECT name FROM public.products", views.Single(static node => node.Name == "v_names").RawBodyExpression);
        Assert.AreEqual("SELECT id, name FROM public.products", views.Single(static node => node.Name == "mv_summary").RawBodyExpression);
    }

    [TestMethod]
    public void ViewBodyPreservesNestedAndQuotedAsText()
    {
        var parser = new PgQueryParser();
        var tree = parser.Parse(
            "CREATE VIEW public.v AS SELECT 'x AS y' AS label, (SELECT value FROM public.t WHERE note = 'AS') AS nested;");

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual(
            "SELECT 'x AS y' AS label, (SELECT value FROM public.t WHERE note = 'AS') AS nested",
            tree.Views.Single().RawBodyExpression);
    }

    [TestMethod]
    public void MaterializedViewBodyExcludesTrailingDataClause()
    {
        var parser = new PgQueryParser();
        var tree = parser.Parse(
            "CREATE MATERIALIZED VIEW public.with_data AS SELECT 'WITH NO DATA' AS value WITH DATA;");

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual("SELECT 'WITH NO DATA' AS value", tree.Views.Single().RawBodyExpression);
    }

    [TestMethod]
    public void ViewBodyUsesUtf8StatementOffsets()
    {
        var parser = new PgQueryParser();
        var tree = parser.Parse(
            "SELECT 'é'; CREATE VIEW public.v AS SELECT 'naïve' AS value;");

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual("SELECT 'naïve' AS value", tree.Views.Single().RawBodyExpression);
    }
}
