using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

[TestClass]
public sealed class MaterializedViewOperationTests
{
    [TestMethod]
    public void CreateOperationEmitsCreateMaterializedViewForMaterializedView()
    {
        var view = CreateView("public.mv_summary", "mv_summary", "public", isMaterialized: true);

        var operation = new CreateOperation
        {
            NodeId = view.Key,
            NodeKind = ChangeNodeKind.View,
            Node = view,
        };

        var sql = operation.GenerateSql();

        StringAssert.StartsWith(sql, "CREATE MATERIALIZED VIEW \"public\".\"mv_summary\" AS");
        Assert.IsFalse(sql.Contains("OR REPLACE"), "Materialized views must not use OR REPLACE.");
    }

    [TestMethod]
    public void CreateOperationEmitsCreateOrReplaceViewForRegularView()
    {
        var view = CreateView("public.v_names", "v_names", "public", isMaterialized: false);

        var operation = new CreateOperation
        {
            NodeId = view.Key,
            NodeKind = ChangeNodeKind.View,
            Node = view,
        };

        var sql = operation.GenerateSql();

        StringAssert.StartsWith(sql, "CREATE OR REPLACE VIEW \"public\".\"v_names\" AS");
    }

    [TestMethod]
    public void DropOperationEmitsDropMaterializedViewForMaterializedView()
    {
        var view = CreateView("public.mv_summary", "mv_summary", "public", isMaterialized: true);

        var operation = new DropOperation
        {
            NodeId = view.Key,
            NodeKind = ChangeNodeKind.View,
            Node = view,
        };

        Assert.AreEqual("DROP MATERIALIZED VIEW \"public\".\"mv_summary\";", operation.GenerateSql());
    }

    [TestMethod]
    public void DropOperationEmitsDropViewForRegularView()
    {
        var view = CreateView("public.v_names", "v_names", "public", isMaterialized: false);

        var operation = new DropOperation
        {
            NodeId = view.Key,
            NodeKind = ChangeNodeKind.View,
            Node = view,
        };

        Assert.AreEqual("DROP VIEW \"public\".\"v_names\";", operation.GenerateSql());
    }

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
            RawBodyExpression = "SELECT id, name FROM public.products",
        };
}
