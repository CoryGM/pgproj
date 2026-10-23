using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-286: a domain's base type was lower-cased, so a domain over <c>"Mytype"</c> and one over <c>mytype</c> compared equal. The base type is compared as PostgreSQL
/// compares names: exactly, after an unquoted name is folded.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DomainQuotedBaseTypeDiffTests
{
    private const string Types = "CREATE DOMAIN public.\"Mytype\" AS int; CREATE DOMAIN public.mytype AS int;";

    [TestMethod]
    public void TheSameQuotedBaseTypeIsNoChange()
    {
        var sql = Types + " CREATE DOMAIN public.dm AS \"Mytype\";";

        Assert.AreEqual(0, DomainChanges(Diff(sql, sql)).Count());
    }

    [TestMethod]
    public void AQualifiedAndAnUnqualifiedSpellingAreNoChange()
    {
        Assert.AreEqual(0, DomainChanges(Diff(Types + " CREATE DOMAIN public.dm AS public.\"Mytype\";", Types + " CREATE DOMAIN public.dm AS \"Mytype\";")).Count());
    }

    [TestMethod]
    public void AnUnquotedNameInAnyCaseIsTheFoldedOne()
    {
        Assert.AreEqual(0, DomainChanges(Diff(Types + " CREATE DOMAIN public.dm AS mytype;", Types + " CREATE DOMAIN public.dm AS MYTYPE;")).Count());
    }

    [TestMethod]
    public void AQuotedMixedCaseBaseTypeAndItsFoldedFormAreAChange()
    {
        var modified = DomainChanges(Diff(Types + " CREATE DOMAIN public.dm AS \"Mytype\";", Types + " CREATE DOMAIN public.dm AS mytype;")).Cast<ModifiedChange>().Single();

        Assert.IsTrue(modified.Details.Any(static detail => detail.PropertyName == nameof(TypeDefinition.DomainBaseType)), string.Join(", ", modified.Details.Select(static detail => detail.PropertyName)));
    }

    private static IEnumerable<ChangeRecord> DomainChanges(ChangeSet changes)
        => changes.Added.Cast<ChangeRecord>().Concat(changes.Modified).Concat(changes.Removed).Where(static change => change.NodeId.EndsWith(".dm", StringComparison.Ordinal));

    private static ChangeSet Diff(string live, string project) => new DiffEngine().Diff(Build(live), Build(project));

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
