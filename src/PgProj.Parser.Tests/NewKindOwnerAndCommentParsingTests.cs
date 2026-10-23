namespace PgProj.Parser.Tests;

/// <summary>
/// G-154: a collation, extended statistics, and a text search dictionary and configuration have an owner (<c>ALTER ... OWNER TO</c>) and a comment
/// (<c>COMMENT ON</c>) that are read instead of being warned about and ignored.
/// </summary>
[TestClass]
public sealed class NewKindOwnerAndCommentParsingTests
{
    [TestMethod]
    [DataRow("ALTER COLLATION app.ci OWNER TO bob;", "Collation")]
    [DataRow("ALTER STATISTICS app.st OWNER TO bob;", "Statistics")]
    [DataRow("ALTER TEXT SEARCH DICTIONARY app.d OWNER TO bob;", "TextSearchDictionary")]
    [DataRow("ALTER TEXT SEARCH CONFIGURATION app.c OWNER TO bob;", "TextSearchConfiguration")]
    public void AnOwnerIsReadWithItsKindSchemaAndName(string sql, string kind)
    {
        var tree = new PgQueryParser().Parse(sql);

        var owner = tree.Owners.Single();
        Assert.AreEqual(0, tree.Diagnostics.Count);
        Assert.AreEqual(kind, owner.ObjectKind);
        Assert.AreEqual("app", owner.SchemaName);
        Assert.AreEqual("bob", owner.OwnerRole);
    }

    [TestMethod]
    public void AnUnqualifiedOwnerTakesTheDefaultSchema()
    {
        var owner = new PgQueryParser().Parse("ALTER COLLATION ci OWNER TO bob;").Owners.Single();

        Assert.AreEqual("public", owner.SchemaName);
    }

    [TestMethod]
    [DataRow("COMMENT ON COLLATION app.ci IS 'hi';", "Collation")]
    [DataRow("COMMENT ON STATISTICS app.st IS 'hi';", "Statistics")]
    [DataRow("COMMENT ON TEXT SEARCH DICTIONARY app.d IS 'hi';", "TextSearchDictionary")]
    [DataRow("COMMENT ON TEXT SEARCH CONFIGURATION app.c IS 'hi';", "TextSearchConfiguration")]
    public void ACommentIsReadWithItsKindSchemaAndName(string sql, string kind)
    {
        var tree = new PgQueryParser().Parse(sql);

        var comment = tree.ObjectComments.Single();
        Assert.AreEqual(0, tree.Diagnostics.Count);
        Assert.AreEqual(kind, comment.ObjectKind);
        Assert.AreEqual("app", comment.SchemaName);
        Assert.AreEqual("hi", comment.CommentText);
    }
}
