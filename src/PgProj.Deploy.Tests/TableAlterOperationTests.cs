using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

[TestClass]
public sealed class TableAlterOperationTests
{
    [TestMethod]
    public void AlterOperationEmitsAddColumnForNewColumn()
    {
        var oldTable = CreateTable([CreateColumn("id", "integer", isNullable: false)]);
        var newTable = CreateTable([
            CreateColumn("id", "integer", isNullable: false),
            CreateColumn("name", "text", isNullable: true),
        ]);

        var sql = BuildAlterSql(oldTable, newTable);

        Assert.AreEqual("ALTER TABLE \"public\".\"orders\" ADD COLUMN \"name\" text;", sql);
    }

    [TestMethod]
    public void AlterOperationEmitsDropColumnForRemovedColumn()
    {
        var oldTable = CreateTable([
            CreateColumn("id", "integer", isNullable: false),
            CreateColumn("name", "text", isNullable: true),
        ]);
        var newTable = CreateTable([CreateColumn("id", "integer", isNullable: false)]);

        var sql = BuildAlterSql(oldTable, newTable);

        Assert.AreEqual("ALTER TABLE \"public\".\"orders\" DROP COLUMN \"name\";", sql);
    }

    [TestMethod]
    public void AlterOperationEmitsSetNotNullForNullabilityChange()
    {
        var oldTable = CreateTable([CreateColumn("name", "text", isNullable: true)]);
        var newTable = CreateTable([CreateColumn("name", "text", isNullable: false)]);

        var sql = BuildAlterSql(oldTable, newTable);

        Assert.AreEqual("ALTER TABLE \"public\".\"orders\" ALTER COLUMN \"name\" SET NOT NULL;", sql);
    }

    [TestMethod]
    public void AlterOperationEmitsDropNotNullForNullabilityChange()
    {
        var oldTable = CreateTable([CreateColumn("name", "text", isNullable: false)]);
        var newTable = CreateTable([CreateColumn("name", "text", isNullable: true)]);

        var sql = BuildAlterSql(oldTable, newTable);

        Assert.AreEqual("ALTER TABLE \"public\".\"orders\" ALTER COLUMN \"name\" DROP NOT NULL;", sql);
    }

    [TestMethod]
    public void AlterOperationEmitsSetDefaultForDefaultChange()
    {
        var oldTable = CreateTable([CreateColumn("status", "text", rawDefaultExpression: null)]);
        var newTable = CreateTable([CreateColumn("status", "text", rawDefaultExpression: "'pending'")]);

        var sql = BuildAlterSql(oldTable, newTable);

        Assert.AreEqual("ALTER TABLE \"public\".\"orders\" ALTER COLUMN \"status\" SET DEFAULT 'pending';", sql);
    }

    [TestMethod]
    public void AlterOperationEmitsDropDefaultForDefaultChange()
    {
        var oldTable = CreateTable([CreateColumn("status", "text", rawDefaultExpression: "'pending'")]);
        var newTable = CreateTable([CreateColumn("status", "text", rawDefaultExpression: null)]);

        var sql = BuildAlterSql(oldTable, newTable);

        Assert.AreEqual("ALTER TABLE \"public\".\"orders\" ALTER COLUMN \"status\" DROP DEFAULT;", sql);
    }

    [TestMethod]
    public void AlterOperationEmitsMultipleStatementsForMultipleColumnChanges()
    {
        var oldTable = CreateTable([
            CreateColumn("id", "integer", isNullable: false),
            CreateColumn("legacy", "text", isNullable: true),
        ]);
        var newTable = CreateTable([
            CreateColumn("id", "integer", isNullable: true),
            CreateColumn("name", "text", isNullable: false),
        ]);

        var sql = BuildAlterSql(oldTable, newTable);

        Assert.AreEqual(
            "ALTER TABLE \"public\".\"orders\" DROP COLUMN \"legacy\";\n" +
            "ALTER TABLE \"public\".\"orders\" ALTER COLUMN \"id\" DROP NOT NULL;\n" +
            "ALTER TABLE \"public\".\"orders\" ADD COLUMN \"name\" text NOT NULL;",
            sql);
    }

    [TestMethod]
    public void AlterOperationEmitsAlterColumnTypeWithUsingCastForTypeChange()
    {
        var oldTable = CreateTable([CreateColumn("quantity", "integer")]);
        var newTable = CreateTable([CreateColumn("quantity", "bigint")]);

        var sql = BuildAlterSql(oldTable, newTable);

        Assert.AreEqual(
            "ALTER TABLE \"public\".\"orders\" ALTER COLUMN \"quantity\" TYPE bigint USING \"quantity\"::bigint;",
            sql);
    }

    [TestMethod]
    public void AlterOperationEmitsRenameColumnForExplicitRenameAnnotation()
    {
        var oldTable = CreateTable([
            CreateColumn("id", "integer", isNullable: false),
            CreateColumn("legacy_name", "text", isNullable: true),
        ]);
        var newTable = CreateTable([
            CreateColumn("id", "integer", isNullable: false),
            CreateColumn("display_name", "text", isNullable: true, renamedFrom: "legacy_name"),
        ]);

        var sql = BuildAlterSql(oldTable, newTable);

        Assert.AreEqual(
            "ALTER TABLE \"public\".\"orders\" RENAME COLUMN \"legacy_name\" TO \"display_name\";",
            sql);
    }

    [TestMethod]
    public void AlterOperationEmitsRenameColumnBeforeOtherAlterationsForSameColumn()
    {
        var oldTable = CreateTable([CreateColumn("legacy_name", "text", isNullable: true)]);
        var newTable = CreateTable([CreateColumn("display_name", "text", isNullable: false, renamedFrom: "legacy_name")]);

        var sql = BuildAlterSql(oldTable, newTable);

        Assert.AreEqual(
            "ALTER TABLE \"public\".\"orders\" RENAME COLUMN \"legacy_name\" TO \"display_name\";\n" +
            "ALTER TABLE \"public\".\"orders\" ALTER COLUMN \"display_name\" SET NOT NULL;",
            sql);
    }

    [TestMethod]
    public void AlterOperationReportsNoChangesWhenColumnsAreIdentical()
    {
        var table = CreateTable([CreateColumn("id", "integer", isNullable: false)]);

        var sql = BuildAlterSql(table, table);

        StringAssert.StartsWith(sql, "-- No column-level changes detected");
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
                Details = [],
            },
        }.GenerateSql();

    private static TableDefinition CreateTable(IReadOnlyList<ColumnDefinition> columns)
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
            []);

    private static ColumnDefinition CreateColumn(
        string name,
        string dataType,
        bool? isNullable = null,
        string? rawDefaultExpression = null,
        string? renamedFrom = null)
        => new(
            $"public.orders.{name}",
            name,
            "public",
            "orders",
            dataType,
            dataType,
            isNullable,
            rawDefaultExpression,
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
            renamedFrom);
}
