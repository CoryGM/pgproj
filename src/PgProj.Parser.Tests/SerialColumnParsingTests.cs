namespace PgProj.Parser.Tests;

[TestClass]
public sealed class SerialColumnParsingTests
{
    [TestMethod]
    public void SerialColumnIsNormalizedToIntegerWithSynthesizedDefault()
    {
        var tree = new PgQueryParser().Parse(
            "CREATE TABLE public.orders (id SERIAL PRIMARY KEY);");

        var table = tree.Nodes.OfType<TableNode>().Single();
        var column = table.Columns.Single(c => c.Name == "id");

        Assert.IsTrue(column.IsSerial);
        Assert.AreEqual("integer", column.RawType);
        Assert.AreEqual("integer", column.DataType);
        Assert.AreEqual(false, column.IsNullable);
        Assert.AreEqual("nextval('public.orders_id_seq'::regclass)", column.RawDefaultExpression);
    }

    [TestMethod]
    public void BigSerialColumnIsNormalizedToBigint()
    {
        var tree = new PgQueryParser().Parse(
            "CREATE TABLE public.orders (id BIGSERIAL);");

        var table = tree.Nodes.OfType<TableNode>().Single();
        var column = table.Columns.Single(c => c.Name == "id");

        Assert.IsTrue(column.IsSerial);
        Assert.AreEqual("bigint", column.RawType);
        Assert.AreEqual("nextval('public.orders_id_seq'::regclass)", column.RawDefaultExpression);
    }

    [TestMethod]
    public void SmallSerialColumnIsNormalizedToSmallint()
    {
        var tree = new PgQueryParser().Parse(
            "CREATE TABLE public.orders (id SMALLSERIAL);");

        var table = tree.Nodes.OfType<TableNode>().Single();
        var column = table.Columns.Single(c => c.Name == "id");

        Assert.IsTrue(column.IsSerial);
        Assert.AreEqual("smallint", column.RawType);
        Assert.AreEqual("nextval('public.orders_id_seq'::regclass)", column.RawDefaultExpression);
    }

    [TestMethod]
    public void NonSerialColumnIsNotFlaggedAsSerial()
    {
        var tree = new PgQueryParser().Parse(
            "CREATE TABLE public.orders (id integer, name text);");

        var table = tree.Nodes.OfType<TableNode>().Single();
        foreach (var column in table.Columns)
        {
            Assert.IsFalse(column.IsSerial, $"Column '{column.Name}' should not be flagged as serial.");
        }
    }

    /// <summary>G-58: an unqualified sequence name only resolves for a table on the search path.</summary>
    [TestMethod]
    [DataRow("CREATE TABLE app.orders (id SERIAL);", "nextval('app.orders_id_seq'::regclass)")]
    [DataRow("CREATE TABLE public.orders (id BIGSERIAL);", "nextval('public.orders_id_seq'::regclass)")]
    [DataRow("CREATE TABLE orders (id SERIAL);", "nextval('orders_id_seq'::regclass)")]
    [DataRow("CREATE TABLE \"My App\".\"Orders\" (id SERIAL);", "nextval('\"My App\".\"Orders_id_seq\"'::regclass)")]
    public void SerialDefaultIsSchemaQualifiedAndQuotedOnlyWhenNeeded(string sql, string expected)
    {
        var column = new PgQueryParser().Parse(sql).Nodes.OfType<TableNode>().Single().Columns.Single();

        Assert.AreEqual(expected, column.RawDefaultExpression);
    }
}
