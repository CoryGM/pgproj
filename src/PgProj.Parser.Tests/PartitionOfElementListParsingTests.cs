namespace PgProj.Parser.Tests;

/// <summary>G-266: the element list of <c>PARTITION OF parent (...)</c> is read as written, and its column options are not columns.</summary>
[TestClass]
public sealed class PartitionOfElementListParsingTests
{
    [TestMethod]
    public void TheListIsReadAsWrittenAndItsOptionsAreNotColumns()
    {
        var tree = new PgQueryParser().Parse("""
            CREATE TABLE app.c PARTITION OF "App"."Parent" ( b WITH OPTIONS DEFAULT (1 + 2), a CONSTRAINT c_an NOT NULL, CONSTRAINT c_ck CHECK (b > 0) ) FOR VALUES IN (1, 2);
            """);

        var table = tree.Nodes.OfType<TableNode>().Single();
        Assert.AreEqual("( b WITH OPTIONS DEFAULT (1 + 2), a CONSTRAINT c_an NOT NULL, CONSTRAINT c_ck CHECK (b > 0) )", table.PartitionElementsText);
        Assert.IsEmpty(table.Columns);
        Assert.IsEmpty(tree.Columns, "A column option is not a column.");
        var listedDefault = table.InheritedColumnDefaults.Single();
        Assert.AreEqual("b", listedDefault.ColumnName);
        Assert.AreEqual("(1 + 2)", listedDefault.RawExpression);
        Assert.IsTrue(listedDefault.IsInPartitionOfList);
        var notNull = table.InheritedColumnNotNulls.Single();
        Assert.AreEqual("a", notNull.ColumnName);
        Assert.AreEqual("c_an", notNull.Name);
        var check = tree.Nodes.OfType<ConstraintNode>().Single();
        Assert.AreEqual("c_ck", check.Name);
        Assert.IsTrue(check.IsInPartitionOfList);
        Assert.IsEmpty(tree.Diagnostics);
    }

    [TestMethod]
    [DataRow("CREATE TABLE public.c PARTITION OF public.p FOR VALUES FROM (1) TO (10);")]
    [DataRow("CREATE TABLE public.c PARTITION OF public.p DEFAULT;")]
    [DataRow("CREATE TABLE public.c PARTITION OF public.p FOR VALUES WITH (MODULUS 2, REMAINDER 0);")]
    public void APartitionWithoutAListHasNone(string sql)
    {
        var table = new PgQueryParser().Parse(sql).Nodes.OfType<TableNode>().Single();

        Assert.IsNull(table.PartitionElementsText);
        Assert.IsEmpty(table.InheritedColumnDefaults);
        Assert.IsEmpty(table.InheritedColumnNotNulls);
    }

    [TestMethod]
    public void AnOrdinaryTablesConstraintsAreNotInAPartitionList()
    {
        var tree = new PgQueryParser().Parse("CREATE TABLE public.t (a integer, CONSTRAINT t_ck CHECK (a > 0));");

        Assert.IsFalse(tree.Nodes.OfType<ConstraintNode>().Single().IsInPartitionOfList);
        Assert.IsNull(tree.Nodes.OfType<TableNode>().Single().PartitionElementsText);
    }
}
