namespace PgProj.Parser.Tests;

/// <summary>
/// G-133: <c>CREATE FOREIGN TABLE</c> is read as a table with its server and options, and each column with its own; the forms <c>pg_dump</c> writes after it
/// (<c>ALTER FOREIGN TABLE ... ALTER COLUMN ... OPTIONS</c>, <c>... OWNER TO</c>, <c>COMMENT ON FOREIGN TABLE</c>) are read onto it.
/// </summary>
[TestClass]
public sealed class ForeignTableParsingTests
{
    [TestMethod]
    public void TheTableItsServerAndEveryOptionAreRead()
    {
        var tree = Parse("""
            CREATE FOREIGN TABLE app.remote_orders (
                id integer OPTIONS (column_name 'order_id') NOT NULL,
                note text COLLATE "C" DEFAULT 'none',
                CONSTRAINT positive CHECK (id > 0)
            ) SERVER remote OPTIONS (schema_name 'sales', table_name 'orders');
            """);

        var table = tree.Nodes.OfType<TableNode>().Single();
        Assert.AreEqual("remote_orders", table.Name);
        Assert.AreEqual("remote", table.ForeignServer);
        CollectionAssert.AreEqual(new[] { new ForeignOptionNode("schema_name", "sales"), new ForeignOptionNode("table_name", "orders") }, table.ForeignOptions.ToArray());
        CollectionAssert.AreEqual(new[] { new ForeignOptionNode("column_name", "order_id") }, table.Columns[0].ForeignOptions.ToArray());
        Assert.IsFalse(table.Columns[0].IsNullable ?? true);
        Assert.IsEmpty(table.Columns[1].ForeignOptions);
        Assert.AreEqual("'none'", table.Columns[1].RawDefaultExpression);
        Assert.AreEqual("positive", tree.Nodes.OfType<ConstraintNode>().Single().Name);
        Assert.IsEmpty(tree.Diagnostics);
    }

    [TestMethod]
    public void APartitionThatIsAForeignTableIsAPartitionWithItsServer()
    {
        var tree = Parse("CREATE FOREIGN TABLE app.orders_2025 PARTITION OF app.orders FOR VALUES FROM ('2025-01-01') TO ('2026-01-01') SERVER archive;");

        var table = tree.Nodes.OfType<TableNode>().Single();
        Assert.AreEqual("orders", table.PartitionOfTableName);
        Assert.AreEqual("archive", table.ForeignServer);
        Assert.IsEmpty(tree.Diagnostics);
    }

    [TestMethod]
    public void PgDumpsColumnOptionsOwnerAndCommentAreReadOntoTheTable()
    {
        var trees = AlterTableColumnApplier.Apply([Parse("""
            CREATE FOREIGN TABLE app.ft (a integer, b text) SERVER remote;
            ALTER FOREIGN TABLE ONLY app.ft ALTER COLUMN a OPTIONS (column_name 'a_remote');
            ALTER FOREIGN TABLE app.ft ALTER COLUMN b OPTIONS (ADD x '1', y '2');
            ALTER FOREIGN TABLE app.ft ALTER COLUMN b OPTIONS (SET x '3', DROP y);
            ALTER FOREIGN TABLE app.ft OWNER TO app_owner;
            COMMENT ON FOREIGN TABLE app.ft IS 'remote rows';
            """)]);

        var tree = trees.Single();
        var table = tree.Nodes.OfType<TableNode>().Single();
        CollectionAssert.AreEqual(new[] { new ForeignOptionNode("column_name", "a_remote") }, table.Columns[0].ForeignOptions.ToArray());
        CollectionAssert.AreEqual(new[] { new ForeignOptionNode("x", "3") }, table.Columns[1].ForeignOptions.ToArray());
        var owner = tree.Nodes.OfType<OwnerNode>().Single();
        Assert.AreEqual("Table app.ft app_owner", $"{owner.ObjectKind} {owner.SchemaName}.{owner.ObjectName} {owner.OwnerRole}");
        var comment = tree.Nodes.OfType<ObjectCommentNode>().Single();
        Assert.AreEqual("Table ft remote rows", $"{comment.ObjectKind} {comment.TableName} {comment.CommentText}");
        Assert.IsEmpty(tree.Diagnostics, string.Join(" | ", tree.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [TestMethod]
    public void AnAlterForeignTableActionThatIsNotReadIsWarnedAbout()
    {
        var trees = AlterTableColumnApplier.Apply([Parse("CREATE FOREIGN TABLE app.ft (a integer) SERVER remote; ALTER FOREIGN TABLE app.ft OPTIONS (ADD fetch_size '100');")]);

        StringAssert.Contains(trees.Single().Diagnostics.Single().Message, "is not modeled; the action is ignored");
    }

    [TestMethod]
    public void TheOptionChangesKeepAnOptionsPlace()
    {
        var options = ForeignOptionChange.Apply(
            [new ForeignOptionNode("a", "1"), new ForeignOptionNode("b", "2"), new ForeignOptionNode("c", "3")],
            [new ForeignOptionChange("SET", new ForeignOptionNode("b", "9")), new ForeignOptionChange("DROP", new ForeignOptionNode("a", null)), new ForeignOptionChange("ADD", new ForeignOptionNode("d", "4"))]);

        CollectionAssert.AreEqual(new[] { new ForeignOptionNode("b", "9"), new ForeignOptionNode("c", "3"), new ForeignOptionNode("d", "4") }, options.ToArray());
    }

    private static SqlSyntaxTree Parse(string sql) => new PgQueryParser().Parse(sql);
}
