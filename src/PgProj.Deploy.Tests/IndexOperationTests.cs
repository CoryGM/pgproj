using PgProj.Diff;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

[TestClass]
public sealed class IndexOperationTests
{
    [TestMethod]
    public void CreateOperationEmitsRealColumnListForIndex()
    {
        var index = CreateIndex(["customer_id", "order_date"], isUnique: false, accessMethod: null);

        var sql = new CreateOperation
        {
            NodeId = index.Key,
            NodeKind = ChangeNodeKind.Index,
            Node = index,
        }.GenerateSql();

        Assert.AreEqual(
            "CREATE INDEX \"ix_orders_customer_date\" ON \"public\".\"orders\" (\"customer_id\", \"order_date\");",
            sql);
    }

    [TestMethod]
    public void CreateOperationEmitsUniqueKeywordForUniqueIndex()
    {
        var index = CreateIndex(["order_number"], isUnique: true, accessMethod: null);

        var sql = new CreateOperation
        {
            NodeId = index.Key,
            NodeKind = ChangeNodeKind.Index,
            Node = index,
        }.GenerateSql();

        StringAssert.StartsWith(sql, "CREATE UNIQUE INDEX \"ix_orders_number\"");
    }

    [TestMethod]
    public void CreateOperationEmitsUsingClauseForAccessMethod()
    {
        var index = CreateIndex(["data"], isUnique: false, accessMethod: "gin");

        var sql = new CreateOperation
        {
            NodeId = index.Key,
            NodeKind = ChangeNodeKind.Index,
            Node = index,
        }.GenerateSql();

        StringAssert.Contains(sql, "USING gin (\"data\")");
    }

    [TestMethod]
    public void CreateOperationReportsMissingKeyColumnsRatherThanEmittingBlankColumnList()
    {
        var index = CreateIndex([], isUnique: false, accessMethod: null);

        var sql = new CreateOperation
        {
            NodeId = index.Key,
            NodeKind = ChangeNodeKind.Index,
            Node = index,
        }.GenerateSql();

        StringAssert.Contains(sql, "TODO");
        Assert.IsFalse(sql.Contains("()", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CreateOperationEmitsIncludeClauseForIncludeColumns()
    {
        var index = CreateIndex(["customer_id"], isUnique: false, accessMethod: null) with
        {
            IncludeColumns = ["status", "total"],
        };

        var sql = new CreateOperation
        {
            NodeId = index.Key,
            NodeKind = ChangeNodeKind.Index,
            Node = index,
        }.GenerateSql();

        StringAssert.Contains(sql, "INCLUDE (\"status\", \"total\")");
    }

    [TestMethod]
    public void CreateOperationEmitsWhereClauseForPredicate()
    {
        var index = CreateIndex(["customer_id"], isUnique: false, accessMethod: null) with
        {
            RawPredicateExpression = "(deleted_at IS NULL)",
        };

        var sql = new CreateOperation
        {
            NodeId = index.Key,
            NodeKind = ChangeNodeKind.Index,
            Node = index,
        }.GenerateSql();

        StringAssert.Contains(sql, "WHERE deleted_at IS NULL");
    }

    [TestMethod]
    public void CreateOperationEmitsRichKeyElementClauses()
    {
        var index = CreateIndex(["customer_id"], isUnique: true, accessMethod: null) with
        {
            KeyElements =
            [
                new IndexKeyElementDefinition(null, "lower(email)", "C", "text_pattern_ops", [], true, true),
                new IndexKeyElementDefinition("customer_id", null, null, null, [], false, null),
            ],
        };

        var sql = new CreateOperation
        {
            NodeId = index.Key,
            NodeKind = ChangeNodeKind.Index,
            Node = index,
        }.GenerateSql();

        StringAssert.Contains(sql, "(lower(email)) COLLATE \"C\" text_pattern_ops DESC NULLS FIRST, \"customer_id\"");
    }

    [TestMethod]
    public void ModifiedIndexWithReorderedKeyColumnsReportsChange()
    {
        var oldIndex = CreateIndex(["a", "b"], isUnique: false, accessMethod: null);
        var newIndex = CreateIndex(["b", "a"], isUnique: false, accessMethod: null);

        var table = new TableDefinition("public.t", "t", "public", [], [], [], [], [], [], [], [], [], []);

        var oldModel = BuildModel(table, oldIndex);
        var newModel = BuildModel(table, newIndex);

        var diff = new DiffEngine().Execute(oldModel, newModel);

        Assert.AreEqual(1, diff.ModifiedIndexes.Count);
        var details = diff.ModifiedIndexes[0].Details;
        Assert.IsTrue(details.Any(detail => detail.PropertyName == nameof(IndexDefinition.KeyColumns)));
    }

    [TestMethod]
    public void AKeyColumnDifferingOnlyByCaseIsAnotherColumn()
    {
        // PostgreSQL compares names exactly (G-206): "A" and a are different columns, so the index changed.
        var oldIndex = CreateIndex(["A", "B"], isUnique: false, accessMethod: null);
        var newIndex = CreateIndex(["a", "b"], isUnique: false, accessMethod: null);

        var table = new TableDefinition("public.t", "t", "public", [], [], [], [], [], [], [], [], [], []);

        var oldModel = BuildModel(table, oldIndex);
        var newModel = BuildModel(table, newIndex);

        var diff = new DiffEngine().Execute(oldModel, newModel);

        Assert.AreEqual(1, diff.ModifiedIndexes.Count);
    }

    private static DatabaseModel BuildModel(TableDefinition table, IndexDefinition index)
        => DatabaseModel.Empty with
        {
            Tables = new Dictionary<string, TableDefinition>(StringComparer.OrdinalIgnoreCase) { [table.Key] = table },
            Indexes = new Dictionary<string, IndexDefinition>(StringComparer.OrdinalIgnoreCase) { [index.Key] = index with { TableName = table.Name, SchemaName = table.SchemaName } },
        };

    private static IndexDefinition CreateIndex(string[] keyColumns, bool isUnique, string? accessMethod)
        => new(
            "public.ix_orders_customer_date",
            keyColumns.Length switch
            {
                0 => "ix_orders_customer_date",
                1 when isUnique => "ix_orders_number",
                1 => "ix_orders_data",
                _ => "ix_orders_customer_date",
            },
            "public",
            "orders",
            accessMethod,
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            [])
        {
            KeyColumns = keyColumns,
            IsUnique = isUnique,
        };
}
