using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-156: a column's collation is compared as a collation, not as text. The database reports a built-in one as <c>pg_catalog."C"</c> and a project writes <c>"C"</c>; those are
/// the same, and a different collation, or one where there was none, is still a change.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ColumnCollationDiffTests
{
    private static string Sql(string collation) => $"CREATE SCHEMA app; CREATE COLLATION app.ci (provider = icu, locale = 'und'); CREATE TABLE app.t (id int, m text{collation});";

    [TestMethod]
    [DataRow(" COLLATE \"C\"", " COLLATE pg_catalog.\"C\"")]
    [DataRow(" COLLATE \"en_US\"", " COLLATE pg_catalog.\"en_US\"")]
    [DataRow(" COLLATE app.ci", " COLLATE ci")]
    [DataRow("", " COLLATE \"default\"")]
    public void TheSameCollationWrittenTwoWaysReportsNothing(string first, string second)
    {
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(Sql(first)), Build(Sql(second)))));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(Build(Sql(second)), Build(Sql(first)))));
    }

    [TestMethod]
    [DataRow(" COLLATE \"C\"", " COLLATE \"POSIX\"")]
    [DataRow("", " COLLATE \"C\"")]
    [DataRow(" COLLATE \"C\"", "")]
    [DataRow(" COLLATE app.ci", " COLLATE \"C\"")]
    public void ADifferentCollationIsAChangeOnTheColumn(string before, string after)
    {
        var changes = new DiffEngine().Diff(Build(Sql(before)), Build(Sql(after)));

        Assert.IsTrue(changes.Modified.Any(static change => change.Details.Any(static detail => detail.PropertyName == "ModifiedColumns")
            || change.Details.Any(static detail => detail.PropertyName == "CollationName")), "Expected the column to be reported as changed.");
    }

    private static bool IsEmpty(ChangeSet diff) => diff.Added.Count + diff.Removed.Count + diff.Modified.Count == 0;

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
