using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MaterializedViewLiveLoaderTests
{
    [TestMethod]
    public void GenerateSnapshotSql_EmitsCreateMaterializedViewForMaterializedViewSnapshot()
    {
        var snapshotSql = LiveDatabaseModelLoader.GenerateSnapshotSql(
            [new LiveSchemaSnapshot("public")],
            [new LiveTableSnapshot("public", "products",
                [new LiveColumnSnapshot("id", "int", false, null, false, null, null)])],
            [],
            [],
            [new LiveViewSnapshot("public", "mv_summary", "SELECT id FROM public.products", IsMaterialized: true)]);

        Assert.IsTrue(
            snapshotSql.Contains("CREATE MATERIALIZED VIEW", StringComparison.Ordinal),
            "Expected CREATE MATERIALIZED VIEW in snapshot SQL.");
        Assert.IsFalse(
            snapshotSql.Contains("CREATE OR REPLACE VIEW", StringComparison.Ordinal),
            "Materialized view snapshot must not use CREATE OR REPLACE VIEW.");
    }

    [TestMethod]
    public void GenerateSnapshotSql_EmitsCreateOrReplaceViewForRegularViewSnapshot()
    {
        var snapshotSql = LiveDatabaseModelLoader.GenerateSnapshotSql(
            [new LiveSchemaSnapshot("public")],
            [new LiveTableSnapshot("public", "products",
                [new LiveColumnSnapshot("id", "int", false, null, false, null, null)])],
            [],
            [],
            [new LiveViewSnapshot("public", "v_names", "SELECT id FROM public.products", IsMaterialized: false)]);

        Assert.IsTrue(
            snapshotSql.Contains("CREATE OR REPLACE VIEW", StringComparison.Ordinal),
            "Expected CREATE OR REPLACE VIEW in snapshot SQL.");
    }

    [TestMethod]
    public void BuildModelFromSnapshotSql_MaterializedViewIsLoadedWithIsMaterializedTrue()
    {
        var snapshotSql = LiveDatabaseModelLoader.GenerateSnapshotSql(
            [new LiveSchemaSnapshot("public")],
            [new LiveTableSnapshot("public", "mv_products_test",
                [
                    new LiveColumnSnapshot("id", "int", false, null, false, null, null),
                    new LiveColumnSnapshot("name", "text", false, null, false, null, null)
                ])],
            [],
            [],
            [new LiveViewSnapshot("public", "mv_summary_test", "SELECT id, name FROM public.mv_products_test", IsMaterialized: true)]);

        var model = CreateLoader().BuildModelFromSnapshotSql(snapshotSql);

        Assert.IsTrue(model.Views.ContainsKey("public.mv_summary_test"));
        Assert.IsTrue(model.Views["public.mv_summary_test"].IsMaterialized);
        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [TestMethod]
    public void BuildModelFromSnapshotSql_RegularViewIsLoadedWithIsMaterializedFalse()
    {
        var snapshotSql = LiveDatabaseModelLoader.GenerateSnapshotSql(
            [new LiveSchemaSnapshot("public")],
            [new LiveTableSnapshot("public", "rv_products_test",
                [new LiveColumnSnapshot("id", "int", false, null, false, null, null)])],
            [],
            [],
            [new LiveViewSnapshot("public", "v_names_test", "SELECT id FROM public.rv_products_test", IsMaterialized: false)]);

        var model = CreateLoader().BuildModelFromSnapshotSql(snapshotSql);

        Assert.IsTrue(model.Views.ContainsKey("public.v_names_test"));
        Assert.IsFalse(model.Views["public.v_names_test"].IsMaterialized);
        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [TestMethod]
    public void GenerateSnapshotSql_MaterializedViewAppearsBeforeRegularViewAlphabetically()
    {
        var snapshotSql = LiveDatabaseModelLoader.GenerateSnapshotSql(
            [new LiveSchemaSnapshot("public")],
            [],
            [],
            [],
            [
                new LiveViewSnapshot("public", "z_regular", "SELECT 1", IsMaterialized: false),
                new LiveViewSnapshot("public", "a_materialized", "SELECT 1", IsMaterialized: true)
            ]);

        var materializedIndex = snapshotSql.IndexOf("a_materialized", StringComparison.Ordinal);
        var regularIndex = snapshotSql.IndexOf("z_regular", StringComparison.Ordinal);

        Assert.IsTrue(materializedIndex >= 0);
        Assert.IsTrue(regularIndex >= 0);
        Assert.IsTrue(materializedIndex < regularIndex, "Views are ordered alphabetically; 'a_materialized' must appear before 'z_regular'.");
    }

    private static LiveDatabaseModelLoader CreateLoader()
        => new(new PgQueryParser(), new SemanticBuilder());
}
