using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

[TestClass]
public sealed class TableColumnOperationTests
{
    [TestMethod]
    public void CreateTableEmitsNotNullForNonNullableColumn()
    {
        var table = CreateTable("public.orders", "orders", "public",
            [CreateColumn("id", "public.orders.id", "integer", isNullable: false)]);

        var sql = GenerateSql(table);

        StringAssert.Contains(sql, "\"id\" integer NOT NULL");
    }

    [TestMethod]
    public void CreateTableOmitsNotNullForNullableColumn()
    {
        var table = CreateTable("public.orders", "orders", "public",
            [CreateColumn("notes", "public.orders.notes", "text", isNullable: true)]);

        var sql = GenerateSql(table);

        StringAssert.Contains(sql, "\"notes\" text");
        Assert.IsFalse(sql.Contains("NOT NULL"), "Nullable column must not emit NOT NULL.");
    }

    [TestMethod]
    public void CreateTableEmitsDefaultExpression()
    {
        var table = CreateTable("public.orders", "orders", "public",
            [CreateColumn("status", "public.orders.status", "text", rawDefaultExpression: "'pending'::text")]);

        var sql = GenerateSql(table);

        StringAssert.Contains(sql, "\"status\" text DEFAULT 'pending'::text");
    }

    [TestMethod]
    public void CreateTableEmitsGeneratedAlwaysAsStoredColumn()
    {
        var table = CreateTable("public.orders", "orders", "public",
            [CreateColumn("total", "public.orders.total", "numeric", isGenerated: true, generatedExpression: "(quantity * price)")]);

        var sql = GenerateSql(table);

        StringAssert.Contains(sql, "\"total\" numeric GENERATED ALWAYS AS (quantity * price) STORED");
    }

    [TestMethod]
    public void CreateTableEmitsCollateClause()
    {
        var table = CreateTable("public.orders", "orders", "public",
            [CreateColumn("name", "public.orders.name", "text", collationName: "C")]);

        var sql = GenerateSql(table);

        StringAssert.Contains(sql, "\"name\" text COLLATE \"C\"");
    }

    [TestMethod]
    public void CreateTableCombinesCollateNotNullAndDefault()
    {
        var table = CreateTable("public.orders", "orders", "public",
            [CreateColumn("name", "public.orders.name", "text", isNullable: false, rawDefaultExpression: "'unnamed'::text", collationName: "C")]);

        var sql = GenerateSql(table);

        StringAssert.Contains(sql, "\"name\" text COLLATE \"C\" NOT NULL DEFAULT 'unnamed'::text");
    }

    [TestMethod]
    public void CreateTableDoesNotEmitDefaultForGeneratedColumn()
    {
        var table = CreateTable("public.orders", "orders", "public",
            [CreateColumn("total", "public.orders.total", "numeric", isGenerated: true, generatedExpression: "(quantity * price)", rawDefaultExpression: "0")]);

        var sql = GenerateSql(table);

        Assert.IsFalse(sql.Contains("DEFAULT"), "Generated columns must not also emit DEFAULT.");
    }

    private static string GenerateSql(TableDefinition table)
        => new CreateOperation
        {
            NodeId = table.Key,
            NodeKind = ChangeNodeKind.Table,
            Node = table,
        }.GenerateSql();

    private static TableDefinition CreateTable(string key, string name, string? schemaName, IReadOnlyList<ColumnDefinition> columns)
        => new(key, name, schemaName, columns, [], [], [], [], [], [], [], [], []);

    private static ColumnDefinition CreateColumn(
        string name,
        string key,
        string dataType,
        bool? isNullable = null,
        string? rawDefaultExpression = null,
        bool isGenerated = false,
        string? generatedExpression = null,
        string? collationName = null,
        string? identityGeneration = null)
        => new(key, name, null, null, dataType, dataType, isNullable, rawDefaultExpression, isGenerated, generatedExpression, collationName, null,
            [], [], [], [], [], [], [], [],
            identityGeneration);
}
