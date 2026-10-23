namespace PgProj.Parser.Tests;

/// <summary>
/// G-148: <c>CREATE COLLATION</c> with options is read instead of being warned about and ignored (G-106): its schema and name, provider, locale (or <c>LC_COLLATE</c> and
/// <c>LC_CTYPE</c>), determinism, and rules. <c>FROM</c> another collation is still reported as not modeled.
/// </summary>
[TestClass]
public sealed class CollationParsingTests
{
    [TestMethod]
    public void AnIcuCollationIsReadWithItsLocaleAndDeterminism()
    {
        var tree = new PgQueryParser().Parse("CREATE COLLATION app.ci (provider = icu, locale = 'und-u-ks-level2', deterministic = false);");
        var node = tree.Collations.Single();

        Assert.AreEqual(0, tree.Diagnostics.Count);
        Assert.AreEqual("ci", node.Name);
        Assert.AreEqual("app", node.SchemaName);
        Assert.AreEqual("icu", node.Provider);
        Assert.AreEqual("und-u-ks-level2", node.Locale);
        Assert.IsNull(node.LcCollate);
        Assert.IsFalse(node.IsDeterministic);
    }

    [TestMethod]
    public void ALibcCollationTakesBothLocalesFromLocaleOrFromTheTwoOptions()
    {
        var both = new PgQueryParser().Parse("CREATE COLLATION c1 (locale = 'C');").Collations.Single();
        var apart = new PgQueryParser().Parse("CREATE COLLATION IF NOT EXISTS c2 (lc_collate = 'C', lc_ctype = 'POSIX');").Collations.Single();

        Assert.AreEqual("libc", both.Provider);
        Assert.AreEqual("C", both.LcCollate);
        Assert.AreEqual("C", both.LcCtype);
        Assert.IsNull(both.Locale);
        Assert.IsTrue(both.IsDeterministic);
        Assert.AreEqual("C", apart.LcCollate);
        Assert.AreEqual("POSIX", apart.LcCtype);
        Assert.AreEqual("public", both.SchemaName);
    }

    [TestMethod]
    public void RulesAreReadAndTheVersionIsNot()
    {
        var node = new PgQueryParser().Parse("CREATE COLLATION s.c4 (provider = icu, locale = 'en', rules = '&a<b', version = '1.0');").Collations.Single();

        Assert.AreEqual("&a<b", node.Rules);
    }

    [TestMethod]
    public void TheFromFormIsStillReportedAsNotModeled()
    {
        var tree = new PgQueryParser().Parse("CREATE COLLATION c3 FROM \"C\";");

        Assert.IsEmpty(tree.Collations.ToArray());
        Assert.AreEqual(1, tree.Diagnostics.Count);
        StringAssert.Contains(tree.Diagnostics[0].Message, "CREATE COLLATION ... FROM");
    }

    [TestMethod]
    public void ACreatedCollationIsNoLongerReportedAsUnmodeled()
    {
        Assert.AreEqual(0, new PgQueryParser().Parse("CREATE COLLATION app.ci (provider = icu, locale = 'und');").Diagnostics.Count);
    }
}
