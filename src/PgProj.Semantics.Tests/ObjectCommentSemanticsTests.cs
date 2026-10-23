using PgProj.Parser;

namespace PgProj.Semantics.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ObjectCommentSemanticsTests
{
    [TestMethod]
    public void TableCommentIsAppliedToTableDefinition()
    {
        var model = new SemanticPipeline().Execute(
            [new PgQueryParser().Parse(
                "CREATE TABLE public.accounts (id integer, name text);\n" +
                "COMMENT ON TABLE public.accounts IS 'stores customer accounts';")]);

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static d => d.Message)));

        var table = model.Tables["public.accounts"];
        Assert.AreEqual("stores customer accounts", table.Comment);
    }

    [TestMethod]
    public void ColumnCommentIsAppliedToColumnDefinition()
    {
        var model = new SemanticPipeline().Execute(
            [new PgQueryParser().Parse(
                "CREATE TABLE public.accounts (id integer, name text);\n" +
                "COMMENT ON COLUMN public.accounts.name IS 'the account display name';")]);

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static d => d.Message)));

        var table = model.Tables["public.accounts"];
        var column = table.Columns.Single(c => c.Name == "name");
        Assert.AreEqual("the account display name", column.Comment);

        var idColumn = table.Columns.Single(c => c.Name == "id");
        Assert.IsNull(idColumn.Comment);
    }

    [TestMethod]
    public void TableAndColumnWithoutCommentsHaveNullComment()
    {
        var model = new SemanticPipeline().Execute(
            [new PgQueryParser().Parse("CREATE TABLE public.accounts (id integer, name text);")]);

        Assert.IsFalse(model.HasErrors, string.Join(Environment.NewLine, model.Diagnostics.Select(static d => d.Message)));

        var table = model.Tables["public.accounts"];
        Assert.IsNull(table.Comment);
        foreach (var column in table.Columns)
        {
            Assert.IsNull(column.Comment, $"Column '{column.Name}' should not have a comment.");
        }
    }
}
