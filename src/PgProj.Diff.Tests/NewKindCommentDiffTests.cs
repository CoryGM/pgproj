using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Diff.Tests;

/// <summary>G-154: a comment on a collation, extended statistics object, or text search dictionary or configuration is compared, and the owner of each is compared like any other.</summary>
[TestClass]
[DoNotParallelize]
public sealed class NewKindCommentDiffTests
{
    private const string Objects =
        "CREATE SCHEMA app; CREATE TABLE app.t (a int, b int); "
        + "CREATE COLLATION app.ci (provider = icu, locale = 'und'); "
        + "CREATE STATISTICS app.st ON a, b FROM app.t; "
        + "CREATE TEXT SEARCH DICTIONARY app.d (TEMPLATE = simple); "
        + "CREATE TEXT SEARCH CONFIGURATION app.c (PARSER = pg_catalog.\"default\"); ";

    [TestMethod]
    public void TheSameCommentsReportNothing()
    {
        var sql = Objects + "COMMENT ON COLLATION app.ci IS 'x'; COMMENT ON STATISTICS app.st IS 'x'; COMMENT ON TEXT SEARCH DICTIONARY app.d IS 'x'; COMMENT ON TEXT SEARCH CONFIGURATION app.c IS 'x';";

        var diff = new DiffEngine().Diff(Build(sql), Build(sql));

        Assert.AreEqual(0, diff.Added.Count + diff.Removed.Count + diff.Modified.Count);
    }

    [TestMethod]
    [DataRow("COMMENT ON COLLATION app.ci", ChangeNodeKind.Collation)]
    [DataRow("COMMENT ON STATISTICS app.st", ChangeNodeKind.Statistics)]
    [DataRow("COMMENT ON TEXT SEARCH DICTIONARY app.d", ChangeNodeKind.TextSearchDictionary)]
    [DataRow("COMMENT ON TEXT SEARCH CONFIGURATION app.c", ChangeNodeKind.TextSearchConfiguration)]
    public void ACommentAddedChangedOrRemovedIsReportedAsTheOnlyChange(string statement, ChangeNodeKind kind)
    {
        var without = Build(Objects);
        var first = Build(Objects + $"{statement} IS 'one';");
        var second = Build(Objects + $"{statement} IS 'two';");

        foreach (var (before, after) in new[] { (without, first), (first, second), (second, without) })
        {
            var modified = new DiffEngine().Diff(before, after).Modified.Single(change => change.NodeKind == kind);
            CollectionAssert.AreEqual(new[] { "Comment" }, modified.Details.Select(static detail => detail.PropertyName).ToArray());
        }
    }

    [TestMethod]
    public void AnOwnerOfOneOfTheseKindsIsAddedAndChanged()
    {
        var without = Build(Objects);
        var bob = Build(Objects + "ALTER COLLATION app.ci OWNER TO bob;");
        var amy = Build(Objects + "ALTER COLLATION app.ci OWNER TO amy;");

        Assert.AreEqual(1, new DiffEngine().Diff(without, bob).Added.Count(static change => change.NodeKind == ChangeNodeKind.Owner));
        Assert.AreEqual(1, new DiffEngine().Diff(bob, amy).Modified.Count(static change => change.NodeKind == ChangeNodeKind.Owner));
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
