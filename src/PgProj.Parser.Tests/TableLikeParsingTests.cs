namespace PgProj.Parser.Tests;

/// <summary>G-221: a <c>LIKE</c> clause of <c>CREATE TABLE</c> is read with its source, its options, its text as written, and its place among the table's own columns.</summary>
[TestClass]
public sealed class TableLikeParsingTests
{
    [TestMethod]
    public void TheClauseIsReadAsWrittenWithItsOptionsAndPlace()
    {
        var table = Table("CREATE TABLE app.b (x int, like  app.a INCLUDING ALL EXCLUDING Comments, extra int);");

        var like = table.LikeClauses.Single();
        Assert.AreEqual("app", like.SchemaName);
        Assert.AreEqual("a", like.Name);
        Assert.AreEqual("like app.a INCLUDING ALL EXCLUDING Comments", like.RawText);
        Assert.AreEqual(1, like.OwnColumnsBefore);
        Assert.IsFalse(like.Includes(TableLikeNode.Comments));
        Assert.IsTrue(like.Includes(TableLikeNode.Indexes) && like.Includes(TableLikeNode.Defaults) && like.Includes(TableLikeNode.Statistics));
        CollectionAssert.AreEqual(new[] { "x", "extra" }, table.Columns.Select(static column => column.Name).ToArray());
    }

    [TestMethod]
    public void EachOptionIsItsOwnBit()
    {
        var like = Table("CREATE TABLE b (LIKE a INCLUDING DEFAULTS INCLUDING INDEXES);").LikeClauses.Single();

        Assert.AreEqual(TableLikeNode.Defaults | TableLikeNode.Indexes, like.Options);
        Assert.AreEqual("LIKE a INCLUDING DEFAULTS INCLUDING INDEXES", like.RawText);
    }

    [TestMethod]
    public void AClauseWithNoOptionsCopiesOnlyColumnsAndAnUnwrittenSchemaIsDefaulted()
    {
        var like = Table("CREATE TABLE b (LIKE a);").LikeClauses.Single();

        Assert.AreEqual(0, like.Options);
        Assert.AreEqual("public", like.SchemaName);
        Assert.IsTrue(like.IsSchemaDefaulted);
        Assert.AreEqual(0, like.OwnColumnsBefore);
    }

    [TestMethod]
    public void TwoClausesKeepTheirOrderAndPlaces()
    {
        var likes = Table("CREATE TABLE c (LIKE a, m int, n int, LIKE b INCLUDING CONSTRAINTS);").LikeClauses;

        CollectionAssert.AreEqual(new[] { "a", "b" }, likes.Select(static like => like.Name).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 2 }, likes.Select(static like => like.OwnColumnsBefore).ToArray());
    }

    [TestMethod]
    public void ATableWithoutLikeHasNone()
        => Assert.IsEmpty(Table("CREATE TABLE b (x int);").LikeClauses);

    private static TableNode Table(string sql) => new PgQueryParser().Parse(sql).Nodes.OfType<TableNode>().Single();
}
