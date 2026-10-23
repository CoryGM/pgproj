using PgProj.Parser;

namespace PgProj.Semantics.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MaterializedViewSemanticsTests
{
    [TestMethod]
    public void CreateMaterializedViewIsCapturedWithIsMaterializedTrue()
    {
        const string sql =
            "CREATE MATERIALIZED VIEW public.product_summary AS SELECT id, name FROM public.products;";

        var model = BuildModel(sql);

        Assert.IsTrue(model.Views.ContainsKey("public.product_summary"));
        var view = model.Views["public.product_summary"];
        Assert.AreEqual("product_summary", view.Name);
        Assert.AreEqual("public", view.SchemaName);
        Assert.IsTrue(view.IsMaterialized);
        Assert.AreEqual("SELECT id, name FROM public.products", view.RawBodyExpression);
        Assert.AreEqual("select id,name from public.products", view.NormalizedExpressions.Single());
    }

    [TestMethod]
    public void CreateRegularViewIsCapturedWithIsMaterializedFalse()
    {
        const string sql =
            "CREATE VIEW public.product_names AS SELECT name FROM public.products;";

        var model = BuildModel(sql);

        Assert.IsTrue(model.Views.ContainsKey("public.product_names"));
        var view = model.Views["public.product_names"];
        Assert.AreEqual("product_names", view.Name);
        Assert.IsFalse(view.IsMaterialized);
        Assert.AreEqual("SELECT name FROM public.products", view.RawBodyExpression);
        Assert.AreEqual("select name from public.products", view.NormalizedExpressions.Single());
        Assert.IsFalse(model.Diagnostics.Any(static diagnostic => diagnostic.Message.Contains("does not expose a raw SELECT", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void BothRegularAndMaterializedViewsAreStoredInViewsDictionary()
    {
        const string sql =
            """
            CREATE TABLE public.products (id int, name text);
            CREATE VIEW public.v_names AS SELECT name FROM public.products;
            CREATE MATERIALIZED VIEW public.mv_summary AS SELECT id, name FROM public.products;
            """;

        var model = BuildModel(sql);

        Assert.IsTrue(model.Views.ContainsKey("public.v_names"));
        Assert.IsTrue(model.Views.ContainsKey("public.mv_summary"));
        Assert.IsFalse(model.Views["public.v_names"].IsMaterialized);
        Assert.IsTrue(model.Views["public.mv_summary"].IsMaterialized);
    }

    [TestMethod]
    public void ViewBodyNormalizationPreservesQuotedContentAndCanonicalizesCosmeticDifferences()
    {
        const string sql =
            "CREATE VIEW public.v AS SELECT /* formatting */ 'MiXeD' AS \"DisplayName\", P.ID FROM public.Products AS P WHERE P.ID > 0;";

        var model = BuildModel(sql);
        var view = model.Views["public.v"];

        Assert.AreEqual(
            "SELECT /* formatting */ 'MiXeD' AS \"DisplayName\", P.ID FROM public.Products AS P WHERE P.ID > 0",
            view.RawBodyExpression);
        Assert.AreEqual(
            "select 'MiXeD' as \"DisplayName\",p.id from public.products as p where p.id>0",
            view.NormalizedExpressions.Single());
    }

    [TestMethod]
    public void CosmeticallyDifferentViewBodiesProduceTheSameCanonicalForm()
    {
        var first = BuildModel(
            "CREATE VIEW public.v AS SELECT P.ID FROM public.Products AS P WHERE P.ID > 0;");
        var second = BuildModel(
            "CREATE VIEW PUBLIC.V AS select /* same query */ p.id\nfrom PUBLIC.PRODUCTS as p where p.id>0;");

        Assert.AreEqual(
            first.Views["public.v"].NormalizedExpressions.Single(),
            second.Views["public.v"].NormalizedExpressions.Single());
    }

    private static DatabaseModel BuildModel(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
