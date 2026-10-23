namespace PgProj.Parser.Tests;

[TestClass]
public sealed class ObjectCommentParsingTests
{
    [TestMethod]
    public void CommentOnTableIsCapturedAsObjectCommentNode()
    {
        var tree = new PgQueryParser().Parse(
            "COMMENT ON TABLE public.accounts IS 'stores customer accounts';");

        var comment = tree.Nodes.OfType<ObjectCommentNode>().Single();

        Assert.AreEqual("Table", comment.ObjectKind);
        Assert.AreEqual("public", comment.SchemaName);
        Assert.AreEqual("accounts", comment.TableName);
        Assert.IsNull(comment.ColumnName);
        Assert.AreEqual("stores customer accounts", comment.CommentText);
    }

    [TestMethod]
    public void CommentOnColumnIsCapturedAsObjectCommentNode()
    {
        var tree = new PgQueryParser().Parse(
            "COMMENT ON COLUMN public.accounts.name IS 'the account display name';");

        var comment = tree.Nodes.OfType<ObjectCommentNode>().Single();

        Assert.AreEqual("Column", comment.ObjectKind);
        Assert.AreEqual("public", comment.SchemaName);
        Assert.AreEqual("accounts", comment.TableName);
        Assert.AreEqual("name", comment.ColumnName);
        Assert.AreEqual("the account display name", comment.CommentText);
    }

    [TestMethod]
    public void CommentOnUnqualifiedTableTakesTheDefaultSchema()
    {
        var tree = new PgQueryParser().Parse(
            "COMMENT ON TABLE accounts IS 'unqualified table comment';");

        var comment = tree.Nodes.OfType<ObjectCommentNode>().Single();

        Assert.AreEqual("Table", comment.ObjectKind);
        Assert.AreEqual("public", comment.SchemaName, "An unqualified name is the object in public (G-114).");
        Assert.AreEqual("accounts", comment.TableName);
        Assert.AreEqual("unqualified table comment", comment.CommentText);
    }

    [TestMethod]
    public void CommentOnViewIsCapturedAsObjectCommentNode()
    {
        var tree = new PgQueryParser().Parse(
            "COMMENT ON VIEW public.active_accounts IS 'active accounts only';");

        var comment = tree.Nodes.OfType<ObjectCommentNode>().Single();

        Assert.AreEqual("View", comment.ObjectKind);
        Assert.AreEqual("public", comment.SchemaName);
        Assert.AreEqual("active_accounts", comment.TableName);
        Assert.AreEqual("active accounts only", comment.CommentText);
    }

    [TestMethod]
    public void CommentOnMaterializedViewIsCapturedAsObjectCommentNode()
    {
        var tree = new PgQueryParser().Parse(
            "COMMENT ON MATERIALIZED VIEW public.account_summary IS 'summarized account data';");

        var comment = tree.Nodes.OfType<ObjectCommentNode>().Single();

        Assert.AreEqual("View", comment.ObjectKind);
        Assert.AreEqual("public", comment.SchemaName);
        Assert.AreEqual("account_summary", comment.TableName);
        Assert.AreEqual("summarized account data", comment.CommentText);
    }

    [TestMethod]
    public void CommentOnIndexIsCapturedAsObjectCommentNode()
    {
        var tree = new PgQueryParser().Parse(
            "COMMENT ON INDEX public.accounts_name_idx IS 'index on account name';");

        var comment = tree.Nodes.OfType<ObjectCommentNode>().Single();

        Assert.AreEqual("Index", comment.ObjectKind);
        Assert.AreEqual("public", comment.SchemaName);
        Assert.AreEqual("accounts_name_idx", comment.TableName);
        Assert.AreEqual("index on account name", comment.CommentText);
    }

    [TestMethod]
    public void CommentOnSequenceIsCapturedAsObjectCommentNode()
    {
        var tree = new PgQueryParser().Parse(
            "COMMENT ON SEQUENCE public.accounts_id_seq IS 'accounts id sequence';");

        var comment = tree.Nodes.OfType<ObjectCommentNode>().Single();

        Assert.AreEqual("Sequence", comment.ObjectKind);
        Assert.AreEqual("public", comment.SchemaName);
        Assert.AreEqual("accounts_id_seq", comment.TableName);
        Assert.AreEqual("accounts id sequence", comment.CommentText);
    }

    [TestMethod]
    public void CommentOnTypeIsCapturedAsObjectCommentNode()
    {
        var tree = new PgQueryParser().Parse(
            "COMMENT ON TYPE public.account_status IS 'account status enum';");

        var comment = tree.Nodes.OfType<ObjectCommentNode>().Single();

        Assert.AreEqual("Type", comment.ObjectKind);
        Assert.AreEqual("public", comment.SchemaName);
        Assert.AreEqual("account_status", comment.TableName);
        Assert.AreEqual("account status enum", comment.CommentText);
    }

    [TestMethod]
    public void CommentOnFunctionIsCapturedWithArgumentTypeSignature()
    {
        var tree = new PgQueryParser().Parse(
            "COMMENT ON FUNCTION public.calculate_total(integer, numeric) IS 'calculates order total';");

        var comment = tree.Nodes.OfType<ObjectCommentNode>().Single();

        Assert.AreEqual("Function", comment.ObjectKind);
        Assert.AreEqual("public", comment.SchemaName);
        Assert.AreEqual("calculate_total", comment.TableName);
        Assert.AreEqual("calculates order total", comment.CommentText);
        Assert.IsNotNull(comment.ArgumentTypeSignature);
        StringAssert.Contains(comment.ArgumentTypeSignature, "int4");
        StringAssert.Contains(comment.ArgumentTypeSignature, "numeric");
    }

    [TestMethod]
    public void CommentOnProcedureIsCapturedWithArgumentTypeSignature()
    {
        var tree = new PgQueryParser().Parse(
            "COMMENT ON PROCEDURE public.archive_account(integer) IS 'archives an account';");

        var comment = tree.Nodes.OfType<ObjectCommentNode>().Single();

        Assert.AreEqual("Procedure", comment.ObjectKind);
        Assert.AreEqual("public", comment.SchemaName);
        Assert.AreEqual("archive_account", comment.TableName);
        Assert.AreEqual("archives an account", comment.CommentText);
        Assert.IsNotNull(comment.ArgumentTypeSignature);
        StringAssert.Contains(comment.ArgumentTypeSignature, "int4");
    }

    [TestMethod]
    public void CommentOnDomainIsReadAsACommentOnTheType()
    {
        var tree = new PgQueryParser().Parse("COMMENT ON DOMAIN app.amount IS 'An amount.';");

        var comment = tree.Nodes.OfType<ObjectCommentNode>().Single();

        Assert.AreEqual("Type", comment.ObjectKind, "PostgreSQL keeps one comment for a domain, whichever statement sets it (G-302).");
        Assert.AreEqual("app", comment.SchemaName);
        Assert.AreEqual("amount", comment.TableName);
        Assert.AreEqual("An amount.", comment.CommentText);
        Assert.AreEqual(0, tree.Diagnostics.Count, "COMMENT ON DOMAIN is no longer reported as not modeled.");
    }
}
