using PgProj.Semantics;

namespace PgProj.Diff.Tests;

[TestClass]
public sealed class IdentityColumnDiffTests
{
    [TestMethod]
    public void IdentityGenerationChangeIsDetectedAsColumnModification()
    {
        var oldModel = CreateModel(CreateTable("public.orders", "orders", "public",
            [CreateColumn("id", "public.orders.id", identityGeneration: "ALWAYS")]));

        var newModel = CreateModel(CreateTable("public.orders", "orders", "public",
            [CreateColumn("id", "public.orders.id", identityGeneration: "BY DEFAULT")]));

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(1, result.ModifiedTables);
        var change = result.ModifiedTables[0];
        Assert.IsTrue(
            change.Details.Any(static d =>
                d.PropertyName == "ModifiedColumns"
                && (d.NewValue ?? string.Empty).Contains("id")),
            "Modified column 'id' should be flagged.");
    }

    [TestMethod]
    public void AddingIdentityGenerationIsDetectedAsColumnModification()
    {
        var oldModel = CreateModel(CreateTable("public.orders", "orders", "public",
            [CreateColumn("id", "public.orders.id", identityGeneration: null)]));

        var newModel = CreateModel(CreateTable("public.orders", "orders", "public",
            [CreateColumn("id", "public.orders.id", identityGeneration: "ALWAYS")]));

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(1, result.ModifiedTables);
    }

    [TestMethod]
    public void UnchangedIdentityGenerationProducesNoModification()
    {
        var oldModel = CreateModel(CreateTable("public.orders", "orders", "public",
            [CreateColumn("id", "public.orders.id", identityGeneration: "ALWAYS")]));

        var newModel = CreateModel(CreateTable("public.orders", "orders", "public",
            [CreateColumn("id", "public.orders.id", identityGeneration: "ALWAYS")]));

        var result = new DiffEngine().Execute(oldModel, newModel);

        Assert.HasCount(0, result.ModifiedTables);
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

    private static ColumnDefinition CreateColumn(string name, string key, string? identityGeneration = null)
        => new(key, name, null, null, "integer", "integer", true, null, false, null, null, null,
            [], [], [], [], [], [], [], [],
            identityGeneration);
}
