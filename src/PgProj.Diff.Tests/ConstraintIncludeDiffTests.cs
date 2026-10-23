using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>G-200: a key or exclusion constraint's <c>INCLUDE</c> columns are compared in order, both ways; PostgreSQL's spelling is the same constraint.</summary>
[TestClass]
[DoNotParallelize]
public sealed class ConstraintIncludeDiffTests
{
    private const string T = "CREATE TABLE t (id int, a int, b int, CONSTRAINT t_pk PRIMARY KEY (id){0});";

    [TestMethod]
    [DataRow("", " INCLUDE (a)", DisplayName = "added")]
    [DataRow(" INCLUDE (a)", " INCLUDE (b)", DisplayName = "changed")]
    [DataRow(" INCLUDE (a, b)", " INCLUDE (b, a)", DisplayName = "reordered")]
    public void AChangedIncludeIsReportedBothWays(string before, string after)
    {
        Assert.AreNotEqual(0, ChangeCount(string.Format(T, before), string.Format(T, after)));
        Assert.AreNotEqual(0, ChangeCount(string.Format(T, after), string.Format(T, before)));
    }

    [TestMethod]
    public void TheSameIncludeIsNoChange()
    {
        Assert.AreEqual(0, ChangeCount(
            string.Format(T, " INCLUDE (a)"),
            "CREATE TABLE t (id int, a int, b int); ALTER TABLE t ADD CONSTRAINT t_pk PRIMARY KEY (id) INCLUDE (a);"));
    }

    private static int ChangeCount(string oldSql, string newSql)
    {
        var changes = new DiffEngine().Diff(Build(oldSql), Build(newSql));
        return changes.Added.Count + changes.Removed.Count + changes.Modified.Count;
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
