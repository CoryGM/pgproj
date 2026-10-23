using PgProj.Semantics;

namespace PgProj.Diff.Tests;

[TestClass]
public sealed class ObjectCommentDiffTests
{
    [TestMethod]
    public void ChangedTableCommentIsReportedAsModified()
    {
        var oldModel = CreateModel(CreateTable("public.orders", "orders", "public", [], comment: "old comment"));
        var newModel = CreateModel(CreateTable("public.orders", "orders", "public", [], comment: "new comment"));

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(1, result.ModifiedTables);
        var change = result.ModifiedTables[0];
        var detail = change.Details.Single(static d => d.PropertyName == nameof(TableDefinition.Comment));
        Assert.AreEqual("old comment", detail.OldValue);
        Assert.AreEqual("new comment", detail.NewValue);
    }

    [TestMethod]
    public void ChangedColumnCommentIsReportedAsModified()
    {
        var oldModel = CreateModel(CreateTable("public.orders", "orders", "public",
            [CreateColumn("name", "public.orders.name", comment: "old column comment")]));
        var newModel = CreateModel(CreateTable("public.orders", "orders", "public",
            [CreateColumn("name", "public.orders.name", comment: "new column comment")]));

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(1, result.ModifiedTables);
        var change = result.ModifiedTables[0];
        Assert.IsTrue(
            change.Details.Any(static d => d.PropertyName == "ModifiedColumns" && (d.NewValue ?? string.Empty).Contains("name")),
            "Column with a changed comment should be flagged as modified.");
    }

    [TestMethod]
    public void UnchangedCommentsProduceNoModification()
    {
        var oldModel = CreateModel(CreateTable("public.orders", "orders", "public", [], comment: "same comment"));
        var newModel = CreateModel(CreateTable("public.orders", "orders", "public", [], comment: "same comment"));

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.IsEmpty(result.ModifiedTables);
    }

    private static DatabaseModel CreateModel(TableDefinition table)
        => DatabaseModel.Empty with
        {
            Tables = new Dictionary<string, TableDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                [table.Key] = table,
            },
        };

    private static TableDefinition CreateTable(string key, string name, string? schemaName, IReadOnlyList<ColumnDefinition> columns, string? comment = null)
        => new(key, name, schemaName, columns, [], [], [], [], [], [], [], [], [], comment);

    private static ColumnDefinition CreateColumn(string name, string key, string? comment = null)
        => new(key, name, null, null, "text", "text", true, null, false, null, null, null,
            [], [], [], [], [], [], [], [],
            null, false, null, comment);
}
