using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>G-148: a collation is compared by provider, locale, libc locales, determinism, and rules; the same collation written another way is no change.</summary>
[TestClass]
[DoNotParallelize]
public sealed class CollationDiffTests
{
    private static string Icu(string options) => "CREATE COLLATION app.ci (provider = icu, " + options + ");";

    [TestMethod]
    public void ACollationDiffedWithItselfIsEmpty()
    {
        var model = Build(Icu("locale = 'und-u-ks-level2', deterministic = false"));

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(model, model)));
    }

    [TestMethod]
    [DataRow("locale = 'en_US'", "locale = 'en-us'")]
    [DataRow("locale = 'und', deterministic = true", "locale = 'und'")]
    [DataRow("lc_collate = 'und'", "locale = 'und'")]
    public void TheSameCollationWrittenTwoWaysReportsNothing(string first, string second)
    {
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(Icu(first)), Build(Icu(second)))));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(Icu(second)), Build(Icu(first)))));
    }

    [TestMethod]
    [DataRow("locale = 'und'", "locale = 'de'", "Locale")]
    [DataRow("locale = 'und'", "locale = 'und', deterministic = false", "IsDeterministic")]
    [DataRow("locale = 'und'", "locale = 'und', rules = '&a<b'", "Rules")]
    public void ARealChangeIsReportedWithTheProperty(string first, string second, string property)
    {
        var changes = new DiffEngine().Diff(Build(Icu(first)), Build(Icu(second)));

        var modified = changes.Modified.Single(static change => change.NodeKind == ChangeNodeKind.Collation);
        Assert.IsTrue(modified.Details.Any(detail => detail.PropertyName == property), string.Join(", ", modified.Details.Select(static d => d.PropertyName)));
    }

    [TestMethod]
    public void TheProviderIsCompared()
    {
        var changes = new DiffEngine().Diff(Build("CREATE COLLATION app.ci (locale = 'C');"), Build(Icu("locale = 'und'")));

        Assert.AreEqual(1, changes.Modified.Count(static change => change.NodeKind == ChangeNodeKind.Collation));
    }

    [TestMethod]
    public void AnAddedAndARemovedCollationAreReported()
    {
        var empty = Build("CREATE SCHEMA app;");
        var with = Build(Icu("locale = 'und'"));

        Assert.AreEqual(1, new DiffEngine().Diff(empty, with).Added.Count(static change => change.NodeKind == ChangeNodeKind.Collation));
        Assert.AreEqual(1, new DiffEngine().Diff(with, empty).Removed.Count(static change => change.NodeKind == ChangeNodeKind.Collation));
    }

    private static bool IsEmpty(ChangeSet diff) => diff.Added.Count + diff.Removed.Count + diff.Modified.Count == 0;

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
