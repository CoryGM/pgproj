using PgProj.Parser;

namespace PgProj.Parser.Tests;

/// <summary>G-163: <c>COMMENT ON TRIGGER name ON table</c> is read, not reported as ignored.</summary>
[TestClass]
public sealed class TriggerCommentParsingTests
{
    private static SqlSyntaxTree Parse(string sql) => new PgQueryParser().Parse(sql);

    [TestMethod]
    public void ASchemaQualifiedTableAndTheTriggerNameAreRead()
    {
        var tree = Parse("COMMENT ON TRIGGER audit_orders ON app.orders IS 'Writes the audit row.';");

        var comment = tree.ObjectComments.Single();
        Assert.AreEqual("Trigger", comment.ObjectKind);
        Assert.AreEqual("app", comment.SchemaName);
        Assert.AreEqual("orders", comment.TableName);
        Assert.AreEqual("audit_orders", comment.ObjectName);
        Assert.AreEqual("Writes the audit row.", comment.CommentText);
        Assert.AreEqual(0, tree.Diagnostics.Count(), string.Join("; ", tree.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void AnUnqualifiedTableAQuotedNameAndNullAreRead()
    {
        var comment = Parse("COMMENT ON TRIGGER \"My Trigger\" ON orders IS NULL;").ObjectComments.Single();

        Assert.AreEqual("orders", comment.TableName);
        Assert.AreEqual("My Trigger", comment.ObjectName);
        Assert.IsNull(comment.CommentText);
    }

    [TestMethod]
    public void AnEventTriggersCommentIsStillNotModeled()
    {
        var tree = Parse("COMMENT ON EVENT TRIGGER et IS 'x';");

        Assert.AreEqual(0, tree.ObjectComments.Count());
        Assert.AreEqual(1, tree.Diagnostics.Count());
    }
}
