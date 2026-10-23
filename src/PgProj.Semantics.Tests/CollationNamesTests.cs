namespace PgProj.Semantics.Tests;

/// <summary>G-156: two spellings of a collation's name are the same collation: quotes, <c>pg_catalog.</c>, case, a bare name against a qualified one, and none against <c>default</c>.</summary>
[TestClass]
public sealed class CollationNamesTests
{
    [TestMethod]
    [DataRow("C", "pg_catalog.C")]
    [DataRow("\"C\"", "pg_catalog.\"C\"")]
    [DataRow("en_US", "pg_catalog.\"en_US\"")]
    [DataRow("app.ci", "ci")]
    [DataRow("app.CI", "app.ci")]
    [DataRow(null, "default")]
    [DataRow("", "pg_catalog.\"default\"")]
    [DataRow(null, null)]
    public void TwoSpellingsOfOneCollationAreTheSame(string? first, string? second)
    {
        Assert.IsTrue(CollationNames.Same(first, second));
        Assert.IsTrue(CollationNames.Same(second, first));
    }

    [TestMethod]
    [DataRow("C", "POSIX")]
    [DataRow("app.ci", "other.ci")]
    [DataRow("C", null)]
    [DataRow("app.ci", "C")]
    public void DifferentCollationsAreNotTheSame(string? first, string? second)
    {
        Assert.IsFalse(CollationNames.Same(first, second));
        Assert.IsFalse(CollationNames.Same(second, first));
    }
}
