using PgProj.Parser;

namespace PgProj.Parser.Tests;

/// <summary>G-164: <c>COMMENT ON POLICY name ON table</c> is read, not reported as ignored.</summary>
[TestClass]
public sealed class PolicyCommentParsingTests
{
    private static SqlSyntaxTree Parse(string sql) => new PgQueryParser().Parse(sql);

    [TestMethod]
    public void ASchemaQualifiedTableAndThePolicyNameAreRead()
    {
        var tree = Parse("COMMENT ON POLICY own_rows ON app.orders IS 'Users see their own rows.';");

        var comment = tree.ObjectComments.Single();
        Assert.AreEqual("Policy", comment.ObjectKind);
        Assert.AreEqual("app", comment.SchemaName);
        Assert.AreEqual("orders", comment.TableName);
        Assert.AreEqual("own_rows", comment.ObjectName);
        Assert.AreEqual("Users see their own rows.", comment.CommentText);
        Assert.AreEqual(0, tree.Diagnostics.Count(), string.Join("; ", tree.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void AnUnqualifiedTableAQuotedNameAndNullAreRead()
    {
        var comment = Parse("COMMENT ON POLICY \"Own Rows\" ON orders IS NULL;").ObjectComments.Single();

        Assert.AreEqual("orders", comment.TableName);
        Assert.AreEqual("Own Rows", comment.ObjectName);
        Assert.IsNull(comment.CommentText);
    }
}
