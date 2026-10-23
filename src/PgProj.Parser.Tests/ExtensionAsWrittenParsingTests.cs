namespace PgProj.Parser.Tests;

/// <summary>
/// G-213: <c>CREATE EXTENSION</c> keeps what was written (its text, <c>VERSION</c>, and <c>CASCADE</c>), and <c>COMMENT ON EXTENSION</c> is read as a
/// comment on the extension rather than reported as an unmodeled statement.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ExtensionAsWrittenParsingTests
{
    [TestMethod]
    public void VersionCascadeAndTheTextAsWrittenAreKept()
    {
        var node = Single<ExtensionNode>("create extension if not exists earthdistance  schema geo version '1.1' cascade;");

        Assert.AreEqual("earthdistance", node.Name);
        Assert.AreEqual("geo", node.SchemaName);
        Assert.AreEqual("1.1", node.Version);
        Assert.IsTrue(node.IsCascade);
        Assert.AreEqual("create extension if not exists earthdistance  schema geo version '1.1' cascade", node.WrittenText);
    }

    [TestMethod]
    public void AnExtensionWithNoOptionsHasNoVersionAndNoCascade()
    {
        var node = Single<ExtensionNode>("CREATE EXTENSION citext;");

        Assert.IsNull(node.SchemaName);
        Assert.IsNull(node.Version);
        Assert.IsFalse(node.IsCascade);
        Assert.AreEqual("CREATE EXTENSION citext", node.WrittenText);
    }

    [TestMethod]
    [DataRow("COMMENT ON EXTENSION citext IS 'our text';", "our text")]
    [DataRow("COMMENT ON EXTENSION \"uuid-ossp\" IS NULL;", null)]
    public void ACommentOnAnExtensionIsReadAsOne(string sql, string? expected)
    {
        var tree = new PgQueryParser().Parse(sql);

        var comment = tree.ObjectComments.Single();
        Assert.AreEqual("Extension", comment.ObjectKind);
        Assert.AreEqual(sql.Contains("uuid", StringComparison.Ordinal) ? "uuid-ossp" : "citext", comment.TableName);
        Assert.AreEqual(expected, comment.CommentText);
        Assert.IsEmpty(tree.Diagnostics, string.Join(" | ", tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    private static T Single<T>(string sql)
        where T : SqlNode
        => new PgQueryParser().Parse(sql).Nodes.OfType<T>().Single();
}
