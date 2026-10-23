namespace PgProj.Core.Tests;

/// <summary>G-215: a live materialized view is scripted with its storage parameters, and <c>WITH NO DATA</c> when it is not populated.</summary>
[TestClass]
[DoNotParallelize]
public sealed class LiveMaterializedViewStorageTests
{
    [TestMethod]
    public void TheOptionsAndAnUnpopulatedViewAreScripted()
    {
        var view = new LiveViewSnapshot("public", "mv", " SELECT 1 AS a;", IsMaterialized: true) { StorageOptions = ["fillfactor=50"], IsPopulated = false };

        Assert.AreEqual(" WITH (fillfactor=50)", view.HeaderClause(scaffold: false));
        Assert.AreEqual("SELECT 1 AS a WITH NO DATA", view.ScriptedQuery);

        var files = LiveDatabaseModelLoader.BuildScaffoldFiles([new LiveSchemaSnapshot("public")], [], [], [], [view], [], [], [], [], [], []);
        StringAssert.Contains(files.Single(static file => file.RelativePath.EndsWith("mv.sql", StringComparison.Ordinal)).Content, "CREATE MATERIALIZED VIEW public.mv WITH (fillfactor=50) AS");
    }

    [TestMethod]
    public void TheAccessMethodIsAlwaysReadButScaffoldedOnlyWhenNotTheDefault()
    {
        // G-219: the database read states the access method, which the project's is compared with when it states one; a scaffold leaves the default out.
        var view = new LiveViewSnapshot("public", "mv", " SELECT 1 AS a;", IsMaterialized: true) { AccessMethod = "heap", IsDefaultAccessMethod = true, TablespaceName = "ts1" };
        var other = view with { ViewName = "mv2", AccessMethod = "heap2", IsDefaultAccessMethod = false };

        Assert.AreEqual(" USING heap TABLESPACE ts1", view.HeaderClause(scaffold: false));
        Assert.AreEqual(" TABLESPACE ts1", view.HeaderClause(scaffold: true));
        Assert.AreEqual(" USING heap2 TABLESPACE ts1", other.HeaderClause(scaffold: true));

        var files = LiveDatabaseModelLoader.BuildScaffoldFiles([new LiveSchemaSnapshot("public")], [], [], [], [view, other], [], [], [], [], [], []);
        StringAssert.Contains(files.Single(static file => file.RelativePath.EndsWith("mv.sql", StringComparison.Ordinal)).Content, "CREATE MATERIALIZED VIEW public.mv TABLESPACE ts1 AS");
        StringAssert.Contains(files.Single(static file => file.RelativePath.EndsWith("mv2.sql", StringComparison.Ordinal)).Content, "CREATE MATERIALIZED VIEW public.mv2 USING heap2 TABLESPACE ts1 AS");
    }

    [TestMethod]
    public void APopulatedViewWithoutOptionsHasNeither()
    {
        var view = new LiveViewSnapshot("public", "mv", " SELECT 1 AS a;", IsMaterialized: true);

        Assert.AreEqual(string.Empty, view.HeaderClause(scaffold: false));
        Assert.AreEqual("SELECT 1 AS a", view.ScriptedQuery);
    }
}
