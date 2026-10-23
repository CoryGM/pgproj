using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-185: <c>NULLS FIRST</c> is the default for a <c>DESC</c> key and <c>NULLS LAST</c> for an <c>ASC</c> one, and PostgreSQL reads a key back without the
/// clause when it is the default. A key that states the default is the same index as one that leaves it out; a key that states the other ordering is a difference.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class IndexKeyNullsOrderingDiffTests
{
    private static string Sql(string key) => $"CREATE TABLE public.p (id int, score int); CREATE INDEX p_score ON public.p ({key}, id);";

    [TestMethod]
    [DataRow("score DESC NULLS FIRST", "score DESC")]
    [DataRow("score ASC NULLS LAST", "score")]
    [DataRow("score NULLS LAST", "score ASC")]
    [DataRow("score DESC NULLS LAST", "score DESC NULLS LAST")]
    public void AKeyStatingTheDefaultIsTheSameIndexAsOneThatLeavesItOut(string stated, string implied)
    {
        var first = Build(Sql(stated));
        var second = Build(Sql(implied));

        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(first, second)));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(second, first)));
        Assert.IsTrue(IsEmpty(new DiffEngine().Diff(first, first)));
    }

    [TestMethod]
    [DataRow("score DESC", "score DESC NULLS LAST")]
    [DataRow("score", "score NULLS FIRST")]
    [DataRow("score DESC NULLS FIRST", "score ASC NULLS FIRST")]
    [DataRow("score ASC NULLS LAST", "score DESC NULLS LAST")]
    public void AKeyWithTheOtherNullOrderingIsADifference(string before, string after)
    {
        var changes = new DiffEngine().Diff(Build(Sql(before)), Build(Sql(after)));

        Assert.IsFalse(IsEmpty(changes));
    }

    private static bool IsEmpty(ChangeSet diff) => diff.Added.Count == 0 && diff.Removed.Count == 0 && diff.Modified.Count == 0;

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
