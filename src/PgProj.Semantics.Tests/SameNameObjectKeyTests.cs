using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>
/// G-228: a table, a zero-argument function, a statistics object, a collation, and a text search dictionary and configuration may all have one name in
/// PostgreSQL, since each kind has a namespace of its own. They were all keyed <c>schema.name</c>, so the build crashed ("An item with the same key has
/// already been added"). Each now has a key of its own, and each is still found by the name a statement gives it.
/// </summary>
[TestClass]
public sealed class SameNameObjectKeyTests
{
    private const string Sql = """
        CREATE TABLE public.t (a int, b int);
        CREATE STATISTICS public.t ON a, b FROM public.t;
        CREATE FUNCTION public.t() RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;
        CREATE COLLATION public.t (provider = icu, locale = 'und');
        CREATE TEXT SEARCH DICTIONARY public.t (TEMPLATE = simple);
        CREATE TEXT SEARCH CONFIGURATION public.t (COPY = english);
        ALTER TEXT SEARCH CONFIGURATION public.t ALTER MAPPING FOR word WITH public.t;
        ALTER STATISTICS public.t OWNER TO postgres;
        COMMENT ON FUNCTION public.t() IS 'f';
        CREATE TABLE public.u (n text COLLATE public.t);
        """;

    [TestMethod]
    public void EachKindHasAKeyOfItsOwn()
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse(Sql)]);

        CollectionAssert.IsSubsetOf(
            new[] { "public.t", "statistics:public.t", "public.t()", "collation:public.t", "tsdictionary:public.t", "tsconfiguration:public.t" },
            model.GetAllNodes().Select(static node => node.Key).ToArray());
        Assert.AreEqual(model.GetAllNodes().Count(), model.GetAllNodes().Select(static node => node.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.IsFalse(model.HasErrors, string.Join("\n", model.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [TestMethod]
    public void EachIsStillFoundByTheNameAStatementGivesIt()
    {
        var model = new SemanticPipeline().Execute([new PgQueryParser().Parse(Sql)]);

        Assert.AreEqual("f", model.Functions["public.t()"].Comment);
        CollectionAssert.AreEqual(new[] { "public.t" }, model.TextSearchConfigurations["tsconfiguration:public.t"].NormalizedMappings["word"].ToArray());
        Assert.IsTrue(model.TryGetCollation("public.t", out var collation) && collation is not null);
        Assert.IsTrue(model.Owners.Values.Any(static owner => owner.ObjectKey == "statistics:public.t"));
    }

    [TestMethod]
    [DataRow("statistics:public.t", "public.t")]
    [DataRow("collation:app.ci", "app.ci")]
    [DataRow("tsdictionary:app.d", "app.d")]
    [DataRow("tsconfiguration:app.c", "app.c")]
    [DataRow("public.t()", "public.t()")]
    [DataRow("cast:(int4 AS text)", "cast:(int4 AS text)")]
    public void AMessageShowsTheKeyWithoutTheKindItAlreadyNames(string key, string shown)
        => Assert.AreEqual(shown, DatabaseModel.DisplayKey(key));
}
