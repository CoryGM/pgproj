namespace PgProj.Parser.Tests;

[TestClass]
public sealed class ColumnRenameAnnotationParsingTests
{
    [TestMethod]
    public void ColumnWithRenameAnnotationCapturesRenamedFrom()
    {
        var tree = new PgQueryParser().Parse(
            "CREATE TABLE public.orders (\n" +
            "    id integer,\n" +
            "    -- @renamed-from: legacy_name\n" +
            "    display_name text\n" +
            ");");

        var table = tree.Nodes.OfType<TableNode>().Single();
        var column = table.Columns.Single(c => c.Name == "display_name");

        Assert.AreEqual("legacy_name", column.RenamedFrom);
    }

    [TestMethod]
    public void ColumnWithoutRenameAnnotationHasNullRenamedFrom()
    {
        var tree = new PgQueryParser().Parse(
            "CREATE TABLE public.orders (id integer, name text);");

        var table = tree.Nodes.OfType<TableNode>().Single();
        foreach (var column in table.Columns)
        {
            Assert.IsNull(column.RenamedFrom, $"Column '{column.Name}' should not have a rename annotation.");
        }
    }

    [TestMethod]
    public void UnrelatedCommentAboveColumnDoesNotSetRenamedFrom()
    {
        var tree = new PgQueryParser().Parse(
            "CREATE TABLE public.orders (\n" +
            "    id integer,\n" +
            "    -- just a regular comment\n" +
            "    name text\n" +
            ");");

        var table = tree.Nodes.OfType<TableNode>().Single();
        var column = table.Columns.Single(c => c.Name == "name");

        Assert.IsNull(column.RenamedFrom);
    }
    }
