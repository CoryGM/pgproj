using PgProj.Semantics;

namespace PgProj.Diff.Tests;

[TestClass]
public sealed class MaterializedViewDiffTests
{
    [TestMethod]
    public void AddedMaterializedViewIsReportedAsAdded()
    {
        var oldModel = CreateModel();
        var newModel = CreateModel(CreateView("public.mv_summary", "mv_summary", "public", isMaterialized: true));

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(1, result.AddedViews);
        Assert.AreEqual("public.mv_summary", result.AddedViews[0].Key);
        Assert.IsTrue(result.AddedViews[0].IsMaterialized);
    }

    [TestMethod]
    public void RemovedMaterializedViewIsReportedAsRemoved()
    {
        var oldModel = CreateModel(CreateView("public.mv_summary", "mv_summary", "public", isMaterialized: true));
        var newModel = CreateModel();

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(1, result.RemovedViews);
        Assert.AreEqual("public.mv_summary", result.RemovedViews[0].Key);
        Assert.IsTrue(result.RemovedViews[0].IsMaterialized);
    }

    [TestMethod]
    public void UnchangedMaterializedViewProducesNoModification()
    {
        var view = CreateView("public.mv_summary", "mv_summary", "public", isMaterialized: true);
        var oldModel = CreateModel(view);
        var newModel = CreateModel(view);

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(0, result.ModifiedViews);
    }

    [TestMethod]
    public void ViewComparedWithItselfProducesNoModification()
    {
        var model = CreateModel(CreateViewWithBody("select p.id from public.products as p"));

        var result = new DiffEngine().Execute(model, model);

        Assert.HasCount(0, result.ModifiedViews);
    }

    [TestMethod]
    public void EquivalentCanonicalViewBodiesProduceNoModification()
    {
        var oldModel = CreateModel(CreateViewWithBody("select p.id from public.products as p"));
        var newModel = CreateModel(CreateViewWithBody("select p.id from public.products as p"));

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(0, result.ModifiedViews);
    }

    [TestMethod]
    public void MeaningfulViewBodyChangeIsReportedAsModification()
    {
        var oldModel = CreateModel(CreateViewWithBody("select p.id from public.products as p"));
        var newModel = CreateModel(CreateViewWithBody("select p.name from public.products as p"));

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(1, result.ModifiedViews);
        Assert.IsTrue(result.ModifiedViews[0].Details.Any(static detail =>
            detail.PropertyName == nameof(ViewDefinition.SelectExpressionNode)));
    }

    [TestMethod]
    public void QuotedIdentifierChangeIsReportedAsModification()
    {
        var oldModel = CreateModel(CreateViewWithBody("select p.\"DisplayName\" from public.products as p"));
        var newModel = CreateModel(CreateViewWithBody("select p.\"displayname\" from public.products as p"));

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(1, result.ModifiedViews);
    }

    [TestMethod]
    public void ChangingIsMaterializedFlagIsReportedAsModification()
    {
        var oldModel = CreateModel(CreateView("public.v_orders", "v_orders", "public", isMaterialized: false));
        var newModel = CreateModel(CreateView("public.v_orders", "v_orders", "public", isMaterialized: true));

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(1, result.ModifiedViews);
        var change = result.ModifiedViews[0];
        Assert.AreEqual("public.v_orders", change.NodeId);
        Assert.IsTrue(change.Details.Any(static detail =>
            detail.PropertyName == nameof(ViewDefinition.IsMaterialized)));
    }

    [TestMethod]
    public void MaterializedAndRegularViewsAreDiffedIndependently()
    {
        var oldModel = CreateModel(
            CreateView("public.v_names", "v_names", "public", isMaterialized: false),
            CreateView("public.mv_summary", "mv_summary", "public", isMaterialized: true));

        var newModel = CreateModel(
            CreateView("public.v_names", "v_names", "public", isMaterialized: false));

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(0, result.AddedViews);
        Assert.HasCount(1, result.RemovedViews);
        Assert.AreEqual("public.mv_summary", result.RemovedViews[0].Key);
        Assert.HasCount(0, result.ModifiedViews);
    }

    private static DatabaseModel CreateModel(params ViewDefinition[] views)
        => DatabaseModel.Empty with
        {
            Views = views.ToDictionary(static view => view.Key, StringComparer.OrdinalIgnoreCase),
        };

    private static ViewDefinition CreateView(string key, string name, string? schemaName, bool isMaterialized = false)
        => new(
            key,
            name,
            schemaName,
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [])
        {
            IsMaterialized = isMaterialized,
        };

    private static ViewDefinition CreateViewWithBody(string normalizedBody)
        => CreateView("public.v_products", "v_products", "public") with
        {
            SelectExpressionNode = ExpressionNode.Simple(normalizedBody, []),
            NormalizedExpressions = [normalizedBody],
        };
}
