namespace PgProj.Parser.Tests;

/// <summary>G-181: <c>CREATE RECURSIVE VIEW</c> is kept as the plain view PostgreSQL expands it to.</summary>
[TestClass]
public sealed class RecursiveViewParsingTests
{
    [TestMethod]
    public void ARecursiveViewBodyIsTheExpansionPostgreSqlStores()
    {
        var tree = new PgQueryParser().Parse(
            "CREATE RECURSIVE VIEW public.tree (id, depth) AS SELECT id, 0 FROM public.e WHERE parent IS NULL UNION ALL SELECT e.id, t.depth + 1 FROM public.e JOIN tree t ON e.parent = t.id;");

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.AreEqual(
            "WITH RECURSIVE tree (id, depth) AS (SELECT id, 0 FROM public.e WHERE parent IS NULL UNION ALL SELECT e.id, t.depth + 1 FROM public.e JOIN tree t ON e.parent = t.id) SELECT id, depth FROM tree",
            tree.Views.Single().RawBodyExpression);
    }

    [TestMethod]
    public void QuotedNamesAndAnOptionsClauseAreKept()
    {
        var tree = new PgQueryParser().Parse(
            "CREATE OR REPLACE RECURSIVE VIEW public.\"My Tree\" (\"Id\") WITH (security_barrier) AS VALUES (1) UNION ALL SELECT \"Id\" + 1 FROM \"My Tree\" WHERE \"Id\" < 3;");

        Assert.IsFalse(tree.HasErrors, string.Join(Environment.NewLine, tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var view = tree.Views.Single();
        Assert.AreEqual("My Tree", view.Name);
        Assert.StartsWith("WITH RECURSIVE \"My Tree\" (\"Id\") AS (VALUES (1) UNION ALL", view.RawBodyExpression!, StringComparison.Ordinal);
        Assert.EndsWith(") SELECT \"Id\" FROM \"My Tree\"", view.RawBodyExpression!, StringComparison.Ordinal);
        CollectionAssert.AreEqual(new[] { "security_barrier=true" }, view.Options.ToArray());
    }

    [TestMethod]
    public void APlainViewIsNotChanged()
    {
        var tree = new PgQueryParser().Parse("CREATE VIEW public.v AS WITH RECURSIVE r (n) AS (VALUES (1) UNION ALL SELECT n + 1 FROM r WHERE n < 3) SELECT n FROM r;");

        Assert.AreEqual("WITH RECURSIVE r (n) AS (VALUES (1) UNION ALL SELECT n + 1 FROM r WHERE n < 3) SELECT n FROM r", tree.Views.Single().RawBodyExpression);
    }
}
