using PgProj.Semantics;

namespace PgProj.Diff.Tests;

[TestClass]
public sealed class ColumnRenameDiffTests
{
    [TestMethod]
    public void RenamedColumnIsPairedAsModifiedInsteadOfAddAndRemove()
    {
        var oldModel = CreateModel(CreateTable("public.orders", "orders", "public",
            [CreateColumn("legacy_name", "public.orders.legacy_name")]));

        var newModel = CreateModel(CreateTable("public.orders", "orders", "public",
            [CreateColumn("display_name", "public.orders.display_name", renamedFrom: "legacy_name")]));

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(1, result.ModifiedTables);
        var change = result.ModifiedTables[0];

        Assert.IsFalse(
            result.AddedColumns.Any(c => c.Name == "display_name"),
            "No column should be reported as added when a rename annotation pairs it with the old column.");
        Assert.IsFalse(
            result.RemovedColumns.Any(c => c.Name == "legacy_name"),
            "No column should be reported as removed when a rename annotation pairs it with the old column.");
        Assert.IsTrue(
            change.Details.Any(static d =>
                d.PropertyName == "ModifiedColumns"
                && (d.NewValue ?? string.Empty).Contains("display_name")),
            "Renamed column 'display_name' should be flagged as modified.");
    }

    private static DatabaseModel CreateModel(TableDefinition table)
        => DatabaseModel.Empty with
        {
            Tables = new Dictionary<string, TableDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                [table.Key] = table,
            },
        };

    private static TableDefinition CreateTable(string key, string name, string? schemaName, IReadOnlyList<ColumnDefinition> columns)
        => new(key, name, schemaName, columns, [], [], [], [], [], [], [], [], []);

    private static ColumnDefinition CreateColumn(string name, string key, string? renamedFrom = null)
        => new(key, name, null, null, "text", "text", true, null, false, null, null, null,
            [], [], [], [], [], [], [], [],
            null, false, renamedFrom);
}
