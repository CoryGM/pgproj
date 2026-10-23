using PgProj.Parser;

namespace PgProj.Parser.Tests;

/// <summary>G-160: <c>COMMENT ON SCHEMA</c> is read, not reported as ignored.</summary>
[TestClass]
public sealed class SchemaCommentParsingTests
{
    private static SqlSyntaxTree Parse(string sql) => new PgQueryParser().Parse(sql);

    [TestMethod]
    public void ACommentOnASchemaIsASchemaCommentNamingTheSchema()
    {
        var tree = Parse("COMMENT ON SCHEMA app IS 'The application''s tables.';");

        var comment = tree.ObjectComments.Single();
        Assert.AreEqual("Schema", comment.ObjectKind);
        Assert.AreEqual("app", comment.TableName);
        Assert.AreEqual("app", comment.SchemaName);
        Assert.AreEqual("The application's tables.", comment.CommentText);
        Assert.AreEqual(0, tree.Diagnostics.Count, string.Join("; ", tree.Diagnostics.Select(static d => d.Message)));
    }

    [TestMethod]
    public void ANullCommentAndAQuotedMixedCaseNameAreRead()
    {
        var tree = Parse("COMMENT ON SCHEMA \"My Schema\" IS NULL;");

        var comment = tree.ObjectComments.Single();
        Assert.AreEqual("My Schema", comment.TableName);
        Assert.IsNull(comment.CommentText);
    }

    [TestMethod]
    public void ADollarQuotedCommentKeepsItsLinesAndTags()
    {
        var tree = Parse("COMMENT ON SCHEMA app IS $$Billing.\n\n@owner: Finance$$;");

        Assert.AreEqual("Billing.\n\n@owner: Finance", tree.ObjectComments.Single().CommentText);
    }

    [TestMethod]
    public void OtherCommentsOnObjectsThatAreNotModeledAreStillReported()
    {
        var tree = Parse("COMMENT ON RULE r ON public.t IS 'x';");

        Assert.AreEqual(0, tree.ObjectComments.Count());
        Assert.AreEqual(1, tree.Diagnostics.Count());
    }
}
