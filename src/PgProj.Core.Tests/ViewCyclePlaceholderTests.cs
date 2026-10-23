using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>G-254: a view that is part of a cycle of views is scripted from a placeholder first, as <c>pg_dump</c> writes it.</summary>
[TestClass]
public sealed class ViewCyclePlaceholderTests
{
    [TestMethod]
    public void APlaceholderIsScriptedBeforeTheRealBodyAndBecomesTheViewsFirstDefinition()
    {
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql(
            [new LiveSchemaSnapshot("public")],
            [new LiveTableSnapshot("public", "t", [new LiveColumnSnapshot("a", "bigint", true, null, false, null, null)])],
            [],
            [],
            [
                new LiveViewSnapshot("public", "v2", "SELECT v3.a FROM public.v3") { PlaceholderColumns = [("a", "bigint")] },
                new LiveViewSnapshot("public", "v3", "SELECT t.a FROM public.t"),
            ],
            [],
            [],
            []);

        var placeholder = sql.IndexOf("CREATE VIEW public.v2 AS SELECT NULL::bigint AS a;", StringComparison.Ordinal);
        Assert.IsTrue(placeholder >= 0, sql);
        Assert.IsTrue(sql.IndexOf("CREATE OR REPLACE VIEW public.v2 AS", StringComparison.Ordinal) > placeholder, sql);
        Assert.IsFalse(sql.Contains("public.v3 AS SELECT NULL", StringComparison.Ordinal), sql);

        var model = CreateLoader().BuildModelFromSnapshotSql(sql);
        var initial = model.Views["public.v2"].InitialDefinition;
        Assert.IsNotNull(initial, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
        StringAssert.Contains(initial.RawBodyExpression ?? string.Empty, "NULL::bigint");
    }

    [TestMethod]
    public void AViewOutsideACycleHasNoPlaceholder()
    {
        Assert.AreEqual(string.Empty, new LiveViewSnapshot("public", "v", "SELECT 1").PlaceholderStatement);
    }

    private static LiveDatabaseModelLoader CreateLoader() => new(new PgQueryParser(), new SemanticBuilder());
}
