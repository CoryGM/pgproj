using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

[TestClass]
public sealed class ObjectCommentOperationTests
{
    [TestMethod]
    public void CreateOperationEmitsTableAndColumnComments()
    {
        var table = CreateTable(
            [CreateColumn("name", "text", comment: "the display name")],
            comment: "stores customer orders");

        var sql = new CreateOperation
        {
            NodeId = table.Key,
            NodeKind = ChangeNodeKind.Table,
            Node = table,
        }.GenerateSql();

        StringAssert.Contains(sql, "COMMENT ON TABLE \"public\".\"orders\" IS 'stores customer orders';");
        StringAssert.Contains(sql, "COMMENT ON COLUMN \"public\".\"orders\".\"name\" IS 'the display name';");
    }

    [TestMethod]
    public void CreateOperationOmitsCommentsWhenNotPresent()
    {
        var table = CreateTable([CreateColumn("name", "text")]);

        var sql = new CreateOperation
        {
            NodeId = table.Key,
            NodeKind = ChangeNodeKind.Table,
            Node = table,
        }.GenerateSql();

        Assert.IsFalse(sql.Contains("COMMENT ON", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AlterOperationEmitsCommentOnTableWhenCommentChanges()
    {
        var oldTable = CreateTable([CreateColumn("id", "integer")], comment: "old comment");
        var newTable = CreateTable([CreateColumn("id", "integer")], comment: "new comment");

        var sql = BuildAlterSql(oldTable, newTable);

        StringAssert.Contains(sql, "COMMENT ON TABLE \"public\".\"orders\" IS 'new comment';");
    }

    [TestMethod]
    public void AlterOperationEmitsCommentIsNullWhenCommentIsCleared()
    {
        var oldTable = CreateTable([CreateColumn("id", "integer")], comment: "old comment");
        var newTable = CreateTable([CreateColumn("id", "integer")], comment: null);

        var sql = BuildAlterSql(oldTable, newTable);

        StringAssert.Contains(sql, "COMMENT ON TABLE \"public\".\"orders\" IS NULL;");
    }

    [TestMethod]
    public void AlterOperationEmitsCommentOnColumnWhenColumnCommentChanges()
    {
        var oldTable = CreateTable([CreateColumn("name", "text", comment: "old column comment")]);
        var newTable = CreateTable([CreateColumn("name", "text", comment: "new column comment")]);

        var sql = BuildAlterSql(oldTable, newTable);

        StringAssert.Contains(sql, "COMMENT ON COLUMN \"public\".\"orders\".\"name\" IS 'new column comment';");
    }

    private static string BuildAlterSql(TableDefinition oldTable, TableDefinition newTable)
        => new AlterOperation
        {
            NodeId = newTable.Key,
            NodeKind = ChangeNodeKind.Table,
            Change = new ModifiedChange
            {
                NodeId = newTable.Key,
                NodeKind = ChangeNodeKind.Table,
                OldNode = oldTable,
                NewNode = newTable,
                Details = new DiffEngine().Execute(
                    DatabaseModel.Empty with
                    {
                        Tables = new Dictionary<string, TableDefinition>(StringComparer.OrdinalIgnoreCase)
                        {
                            [oldTable.Key] = oldTable,
                        },
                    },
                    DatabaseModel.Empty with
                    {
                        Tables = new Dictionary<string, TableDefinition>(StringComparer.OrdinalIgnoreCase)
                        {
                            [newTable.Key] = newTable,
                        },
                    }).ModifiedTables[0].Details,
            },
        }.GenerateSql();

    private static TableDefinition CreateTable(IReadOnlyList<ColumnDefinition> columns, string? comment = null)
        => new(
            "public.orders",
            "orders",
            "public",
            columns,
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            comment);

    private static ColumnDefinition CreateColumn(
        string name,
        string dataType,
        string? comment = null)
        => new(
            $"public.orders.{name}",
            name,
            "public",
            "orders",
            dataType,
            dataType,
            null,
            null,
            false,
            null,
            null,
            null,
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            null,
            false,
            null,
            comment);
}
