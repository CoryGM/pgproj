using PgProj.Parser;

namespace PgProj.Semantics.Tests;

/// <summary>G-148: a collation is a model object of its own, keyed by schema and name, with a comparison form for its locale.</summary>
[TestClass]
public sealed class CollationSemanticsTests
{
    [TestMethod]
    public void ACollationIsInTheModelKeyedBySchemaAndName()
    {
        var model = Build("CREATE COLLATION app.ci (provider = icu, locale = 'und-u-ks-level2', deterministic = false);");

        var collation = model.Collations["collation:app.ci"];
        Assert.AreEqual("ci", collation.Name);
        Assert.AreEqual("app", collation.SchemaName);
        Assert.AreEqual("icu", collation.Provider);
        Assert.IsFalse(collation.IsDeterministic);
        Assert.AreEqual(0, model.Diagnostics.Count, string.Join("; ", model.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void ADuplicateCollationIsAnError()
    {
        var model = Build("CREATE COLLATION c (locale = 'C'); CREATE COLLATION c (locale = 'POSIX');");

        Assert.IsTrue(model.HasErrors);
    }

    [TestMethod]
    public void AnIcuLocaleHasOneComparisonFormWhateverItsSeparatorOrCase()
    {
        Assert.AreEqual(CollationLocales.Canonical("icu", "en_US"), CollationLocales.Canonical("icu", "en-us"));
        Assert.AreNotEqual(CollationLocales.Canonical("libc", "en_US"), CollationLocales.Canonical("libc", "en-us"));
        Assert.IsNull(CollationLocales.Canonical("icu", null));
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
