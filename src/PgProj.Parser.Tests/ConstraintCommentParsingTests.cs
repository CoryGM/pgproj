using PgProj.Parser;

namespace PgProj.Parser.Tests;

/// <summary>G-161: <c>COMMENT ON CONSTRAINT name ON table</c> is read; a domain's constraint is still reported as not modeled.</summary>
[TestClass]
public sealed class ConstraintCommentParsingTests
{
    private static SqlSyntaxTree Parse(string sql) => new PgQueryParser().Parse(sql);

    [TestMethod]
    public void ASchemaQualifiedTableAndTheConstraintNameAreRead()
    {
        var tree = Parse("COMMENT ON CONSTRAINT orders_pkey ON app.orders IS 'The key.';");

        var comment = tree.ObjectComments.Single();
        Assert.AreEqual("Constraint", comment.ObjectKind);
        Assert.AreEqual("app", comment.SchemaName);
        Assert.AreEqual("orders", comment.TableName);
        Assert.AreEqual("orders_pkey", comment.ObjectName);
        Assert.AreEqual("The key.", comment.CommentText);
        Assert.AreEqual(0, tree.Diagnostics.Count(), string.Join("; ", tree.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void AnUnqualifiedTableAQuotedNameAndNullAreRead()
    {
        var comment = Parse("COMMENT ON CONSTRAINT \"My Key\" ON orders IS NULL;").ObjectComments.Single();

        Assert.AreEqual("orders", comment.TableName);
        Assert.AreEqual("My Key", comment.ObjectName);
        Assert.IsNull(comment.CommentText);
    }

    [TestMethod]
    public void ADomainsConstraintIsStillNotModeled()
    {
        var tree = Parse("COMMENT ON CONSTRAINT positive ON DOMAIN app.amount IS 'x';");

        Assert.AreEqual(0, tree.ObjectComments.Count());
        Assert.AreEqual(1, tree.Diagnostics.Count());
    }
}
