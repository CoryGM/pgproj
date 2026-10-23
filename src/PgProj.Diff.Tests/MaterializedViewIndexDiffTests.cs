using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>
/// G-171: the indexes of a materialized view are compared like the indexes of a table. They were never looked at, because indexes were
/// reached only through tables, so a view's index was never added, changed, or removed.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class MaterializedViewIndexDiffTests
{
    private const string Prelude = "CREATE SCHEMA s; CREATE TABLE s.t (grp int, amount numeric); ";
    private const string View = "CREATE MATERIALIZED VIEW s.mv AS SELECT grp, sum(amount) AS total FROM s.t GROUP BY grp; ";

    [TestMethod]
    public void AnIndexAddedToAnExistingViewIsAdded()
    {
        var result = Diff(Prelude + View, Prelude + View + "CREATE UNIQUE INDEX mv_grp ON s.mv (grp);");

        CollectionAssert.AreEqual(new[] { "s.mv_grp" }, result.AddedIndexes.Select(static index => index.Key).ToArray());
        Assert.IsEmpty(result.RemovedIndexes);
        Assert.IsEmpty(result.ModifiedIndexes);
    }

    [TestMethod]
    public void AnIndexRemovedFromAViewThatStaysIsRemoved()
    {
        var result = Diff(Prelude + View + "CREATE INDEX mv_grp ON s.mv (grp);", Prelude + View);

        CollectionAssert.AreEqual(new[] { "s.mv_grp" }, result.RemovedIndexes.Select(static index => index.Key).ToArray());
        Assert.IsEmpty(result.AddedIndexes);
    }

    [TestMethod]
    public void AChangedIndexIsModified()
    {
        var result = Diff(Prelude + View + "CREATE INDEX mv_grp ON s.mv (grp);", Prelude + View + "CREATE INDEX mv_grp ON s.mv (grp, total);");

        CollectionAssert.AreEqual(new[] { "s.mv_grp" }, result.ModifiedIndexes.Select(static change => change.NodeId).ToArray());
        Assert.IsEmpty(result.AddedIndexes);
        Assert.IsEmpty(result.RemovedIndexes);
    }

    [TestMethod]
    public void TheIndexesOfANewViewAreAdded()
    {
        var result = Diff(Prelude, Prelude + View + "CREATE UNIQUE INDEX mv_grp ON s.mv (grp); CREATE INDEX mv_total ON s.mv (total);");

        CollectionAssert.AreEquivalent(new[] { "s.mv_grp", "s.mv_total" }, result.AddedIndexes.Select(static index => index.Key).ToArray());
    }

    [TestMethod]
    public void TheIndexesOfARemovedViewAreRemoved()
    {
        var result = Diff(Prelude + View + "CREATE INDEX mv_grp ON s.mv (grp);", Prelude);

        CollectionAssert.AreEqual(new[] { "s.mv_grp" }, result.RemovedIndexes.Select(static index => index.Key).ToArray());
    }

    [TestMethod]
    public void IdenticalIndexesAreNoChange()
    {
        var sql = Prelude + View + "CREATE UNIQUE INDEX mv_grp ON s.mv (grp); CREATE INDEX mv_total ON s.mv (total) WHERE total > 0;";

        var result = Diff(sql, sql);

        Assert.IsEmpty(result.AddedIndexes);
        Assert.IsEmpty(result.RemovedIndexes);
        Assert.IsEmpty(result.ModifiedIndexes);
    }

    [TestMethod]
    public void TheIndexesOfATableAreStillComparedWithAViewPresent()
    {
        var result = Diff(Prelude + View + "CREATE INDEX t_grp ON s.t (grp);", Prelude + View + "CREATE INDEX t_amount ON s.t (amount);");

        CollectionAssert.AreEqual(new[] { "s.t_amount" }, result.AddedIndexes.Select(static index => index.Key).ToArray());
        CollectionAssert.AreEqual(new[] { "s.t_grp" }, result.RemovedIndexes.Select(static index => index.Key).ToArray());
    }

    private static DiffResult Diff(string oldSql, string newSql)
        => new DiffEngine().Execute(Build(oldSql), Build(newSql));

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
