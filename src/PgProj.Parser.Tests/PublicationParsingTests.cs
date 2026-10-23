namespace PgProj.Parser.Tests;

/// <summary>
/// G-134: <c>CREATE PUBLICATION</c> is read instead of being warned about and ignored (G-106): its name, <c>FOR ALL TABLES</c>, the tables with their column lists and row
/// filters, the schemas, and the options. A publication belongs to no schema; the tables and the current schema in it take the default schema.
/// </summary>
[TestClass]
public sealed class PublicationParsingTests
{
    [TestMethod]
    public void APublicationIsReadWithItsTablesFiltersSchemasAndOptions()
    {
        // Act
        var tree = new PgQueryParser().Parse(
            "CREATE PUBLICATION p FOR TABLE s.a (id, n) WHERE (n > 0), b, TABLES IN SCHEMA app WITH (publish = 'insert, update', publish_via_partition_root = true);");
        var node = tree.Publications.Single();

        // Assert
        Assert.AreEqual(0, tree.Diagnostics.Count);
        Assert.AreEqual("p", node.Name);
        Assert.IsNull(node.SchemaName);
        Assert.IsFalse(node.IsAllTables);
        Assert.AreEqual(2, node.Tables.Count);
        Assert.AreEqual("s", node.Tables[0].SchemaName);
        Assert.AreEqual("a", node.Tables[0].TableName);
        CollectionAssert.AreEqual(new[] { "id", "n" }, node.Tables[0].Columns.ToArray());
        Assert.IsNotNull(node.Tables[0].RawFilter);
        StringAssert.Contains(node.Tables[0].FilterAst, "A_Expr");
        Assert.AreEqual("public", node.Tables[1].SchemaName);
        Assert.IsEmpty(node.Tables[1].Columns);
        Assert.IsNull(node.Tables[1].RawFilter);
        CollectionAssert.AreEqual(new[] { "app" }, node.Schemas.ToArray());
        Assert.AreEqual("insert, update", node.Options["publish"]);
        Assert.AreEqual("true", node.Options["publish_via_partition_root"]);
    }

    [TestMethod]
    public void ForAllTablesAndAnEmptyPublicationAreRead()
    {
        var everything = new PgQueryParser().Parse("CREATE PUBLICATION q FOR ALL TABLES;").Publications.Single();
        var empty = new PgQueryParser().Parse("CREATE PUBLICATION r;").Publications.Single();

        Assert.IsTrue(everything.IsAllTables);
        Assert.IsFalse(empty.IsAllTables);
        Assert.IsEmpty(empty.Tables);
        Assert.IsEmpty(empty.Schemas);
    }

    [TestMethod]
    public void TheCurrentSchemaIsTheDefaultSchema()
    {
        var node = new PgQueryParser().Parse("CREATE PUBLICATION r FOR TABLES IN SCHEMA CURRENT_SCHEMA, TABLE t;").Publications.Single();

        CollectionAssert.AreEqual(new[] { "public" }, node.Schemas.ToArray());
        Assert.AreEqual("public", node.Tables.Single().SchemaName);
    }

    [TestMethod]
    public void APublicationIsNoLongerReportedAsUnmodeledAndItsFiltersAreNotStatements()
    {
        var tree = new PgQueryParser().Parse("CREATE PUBLICATION p FOR TABLE public.t WHERE (id > 0);");

        Assert.IsFalse(tree.Diagnostics.Any(static d => d.Message.Contains("CREATE PUBLICATION", StringComparison.Ordinal)));
        Assert.AreEqual(1, tree.Nodes.Count);
    }
}
