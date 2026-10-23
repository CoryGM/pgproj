using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

[TestClass]
public sealed class ViewOperationTests
{
    [TestMethod]
    public void CreateOperationEmitsRealSelectBodyForRegularView()
    {
        var view = CreateView("public.v_names", "v_names", "public", isMaterialized: false, "SELECT name FROM public.products");

        var sql = new CreateOperation
        {
            NodeId = view.Key,
            NodeKind = ChangeNodeKind.View,
            Node = view,
        }.GenerateSql();

        Assert.AreEqual("CREATE OR REPLACE VIEW \"public\".\"v_names\" AS\nSELECT name FROM public.products;", sql);
        Assert.IsFalse(sql.Contains("SELECT 1", StringComparison.Ordinal), sql);
        Assert.IsFalse(sql.Contains("TODO", StringComparison.Ordinal), sql);
    }

    [TestMethod]
    public void CreateOperationEmitsRealSelectBodyForMaterializedView()
    {
        var view = CreateView("public.mv_summary", "mv_summary", "public", isMaterialized: true, "SELECT id, name FROM public.products");

        var sql = new CreateOperation
        {
            NodeId = view.Key,
            NodeKind = ChangeNodeKind.View,
            Node = view,
        }.GenerateSql();

        Assert.AreEqual("CREATE MATERIALIZED VIEW \"public\".\"mv_summary\" AS\nSELECT id, name FROM public.products;", sql);
    }

    [TestMethod]
    public void CreateOperationReportsMissingBodyRatherThanEmittingPlaceholder()
    {
        var view = CreateView("public.v_broken", "v_broken", "public", isMaterialized: false, rawBody: null);

        var sql = new CreateOperation
        {
            NodeId = view.Key,
            NodeKind = ChangeNodeKind.View,
            Node = view,
        }.GenerateSql();

        StringAssert.Contains(sql, "TODO");
        Assert.IsFalse(sql.Contains("SELECT 1", StringComparison.Ordinal), sql);
    }

    [TestMethod]
    public void ModifiedRegularViewWithUnchangedColumnsUsesCreateOrReplaceOnly()
    {
        var oldView = CreateView("public.v_names", "v_names", "public", isMaterialized: false, "SELECT name FROM public.products", "name");
        var newView = CreateView("public.v_names", "v_names", "public", isMaterialized: false, "SELECT upper(name) AS name FROM public.products", "name");

        var batch = GenerateModifiedViewBatch(oldView, newView);

        Assert.AreEqual(1, batch.Operations.Count);
        Assert.IsInstanceOfType<CreateOperation>(batch.Operations[0]);
        StringAssert.Contains(batch.Operations[0].GenerateSql(), "CREATE OR REPLACE VIEW");
    }

    [TestMethod]
    public void ModifiedRegularViewWithChangedColumnsUsesDropThenCreate()
    {
        var oldView = CreateView("public.v_names", "v_names", "public", isMaterialized: false, "SELECT name FROM public.products", "name");
        var newView = CreateView("public.v_names", "v_names", "public", isMaterialized: false, "SELECT name, sku FROM public.products", "name", "sku");

        var batch = GenerateModifiedViewBatch(oldView, newView);

        Assert.AreEqual(2, batch.Operations.Count);
        Assert.IsInstanceOfType<DropOperation>(batch.Operations[0]);
        Assert.IsInstanceOfType<CreateOperation>(batch.Operations[1]);
        StringAssert.Contains(batch.Operations[0].GenerateSql(), "DROP VIEW");
    }

    [TestMethod]
    public void ModifiedMaterializedViewAlwaysUsesDropThenCreate()
    {
        var oldView = CreateView("public.mv_summary", "mv_summary", "public", isMaterialized: true, "SELECT id FROM public.products", "id");
        var newView = CreateView("public.mv_summary", "mv_summary", "public", isMaterialized: true, "SELECT id FROM public.products WHERE id > 0", "id");

        var batch = GenerateModifiedViewBatch(oldView, newView);

        Assert.AreEqual(2, batch.Operations.Count);
        Assert.IsInstanceOfType<DropOperation>(batch.Operations[0]);
        Assert.IsInstanceOfType<CreateOperation>(batch.Operations[1]);
        StringAssert.Contains(batch.Operations[0].GenerateSql(), "DROP MATERIALIZED VIEW");
    }

    [TestMethod]
    public void CreateOperationStatesTheHeaderOptionsAndLeavesTheCheckOptionToTheBody()
    {
        var view = CreateView("public.v", "v", "public", isMaterialized: false, "SELECT id FROM public.t WHERE id > 0 WITH LOCAL CHECK OPTION", "id") with
        {
            Options = ["check_option=local", "security_barrier=true", "security_invoker=true"],
        };

        var sql = new CreateOperation { NodeId = view.Key, NodeKind = ChangeNodeKind.View, Node = view }.GenerateSql();

        Assert.AreEqual(
            "CREATE OR REPLACE VIEW \"public\".\"v\" WITH (security_barrier=true, security_invoker=true) AS\nSELECT id FROM public.t WHERE id > 0 WITH LOCAL CHECK OPTION;",
            sql);
    }

    [TestMethod]
    public void AChangeOnlyInTheOptionsReplacesTheViewInPlaceWithTheFullSet()
    {
        var oldView = CreateView("public.v", "v", "public", isMaterialized: false, "SELECT id FROM public.t", "id") with { Options = ["security_barrier=true"] };
        var newView = oldView with { Options = ["security_invoker=true"] };

        var batch = GenerateModifiedViewBatch(oldView, newView);

        Assert.AreEqual(1, batch.Operations.Count);
        Assert.AreEqual("CREATE OR REPLACE VIEW \"public\".\"v\" WITH (security_invoker=true) AS\nSELECT id FROM public.t;", batch.Operations[0].GenerateSql());
    }

    [TestMethod]
    public void RemovingEveryOptionReplacesTheViewWithNoWithClause()
    {
        var oldView = CreateView("public.v", "v", "public", isMaterialized: false, "SELECT id FROM public.t", "id") with { Options = ["security_barrier=true"] };
        var newView = oldView with { Options = [] };

        var batch = GenerateModifiedViewBatch(oldView, newView);

        Assert.AreEqual("CREATE OR REPLACE VIEW \"public\".\"v\" AS\nSELECT id FROM public.t;", batch.Operations[0].GenerateSql());
    }

    [TestMethod]
    public void CreateOperationWritesTheColumnListOfAViewAndAMaterializedView()
    {
        var view = CreateView("public.v", "v", "public", isMaterialized: false, "SELECT id, name FROM public.e") with { ColumnAliases = ["x", "Y z"], Options = ["security_barrier=true"] };
        var matview = CreateView("public.m", "m", "public", isMaterialized: true, "SELECT id, name FROM public.e") with { ColumnAliases = ["x", "y"] };

        Assert.AreEqual(
            "CREATE OR REPLACE VIEW \"public\".\"v\" (\"x\", \"Y z\") WITH (security_barrier=true) AS\nSELECT id, name FROM public.e;",
            new CreateOperation { NodeId = view.Key, NodeKind = ChangeNodeKind.View, Node = view }.GenerateSql());
        Assert.AreEqual(
            "CREATE MATERIALIZED VIEW \"public\".\"m\" (\"x\", \"y\") AS\nSELECT id, name FROM public.e;",
            new CreateOperation { NodeId = matview.Key, NodeKind = ChangeNodeKind.View, Node = matview }.GenerateSql());
    }

    private static OperationBatch GenerateModifiedViewBatch(ViewDefinition oldView, ViewDefinition newView)
    {
        var changes = new ChangeSet
        {
            Added = [],
            Removed = [],
            Modified =
            [
                new ModifiedChange
                {
                    NodeId = newView.Key,
                    NodeKind = ChangeNodeKind.View,
                    OldNode = oldView,
                    NewNode = newView,
                    Details = [],
                },
            ],
        };

        return new ScriptGenerator().Generate(changes, new DependencyGraph());
    }

    private static ViewDefinition CreateView(
        string key,
        string name,
        string? schemaName,
        bool isMaterialized,
        string? rawBody,
        params string[] outputColumnNames)
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
            RawBodyExpression = rawBody,
            OutputColumnNames = outputColumnNames,
        };
}
