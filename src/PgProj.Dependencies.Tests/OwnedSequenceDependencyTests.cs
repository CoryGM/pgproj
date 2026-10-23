using PgProj.Semantics;

namespace PgProj.Dependencies.Tests;

[TestClass]
public sealed class OwnedSequenceDependencyTests
{
    [TestMethod]
    public void TableDependsOnOwnedSequence()
    {
        var model = CreateModel(
            CreateTable("public.orders", "orders", "public"),
            CreateSequence("public.orders_id_seq", "orders_id_seq", "public", ownedByTable: "public.orders", ownedByColumn: "id"));

        var graph = new DependencyAnalyzer().Analyze(model);

        var tableDependencies = graph.GetDependencies("public.orders");
        Assert.IsTrue(
            tableDependencies.Any(static node => string.Equals(node.NodeId, "public.orders_id_seq", StringComparison.OrdinalIgnoreCase)),
            "Expected public.orders to depend on public.orders_id_seq.");
    }

    [TestMethod]
    public void SequenceAppearsBeforeTableInTopologicalOrder()
    {
        var model = CreateModel(
            CreateTable("public.orders", "orders", "public"),
            CreateSequence("public.orders_id_seq", "orders_id_seq", "public", ownedByTable: "public.orders", ownedByColumn: "id"));

        var graph = new DependencyAnalyzer().Analyze(model);
        var order = graph.TopologicalSort().Select(static node => node.NodeId).ToList();

        var sequenceIndex = order.IndexOf("public.orders_id_seq");
        var tableIndex = order.IndexOf("public.orders");

        Assert.IsTrue(sequenceIndex >= 0, "Sequence must be present in topological order.");
        Assert.IsTrue(tableIndex >= 0, "Table must be present in topological order.");
        Assert.IsTrue(sequenceIndex < tableIndex,
            $"Sequence (index {sequenceIndex}) must appear before table (index {tableIndex}) in topological order.");
    }

    [TestMethod]
    public void SequenceWithoutOwnedByDoesNotAddTableDependency()
    {
        var model = CreateModel(
            CreateTable("public.orders", "orders", "public"),
            CreateSequence("public.standalone_seq", "standalone_seq", "public", ownedByTable: null, ownedByColumn: null));

        var graph = new DependencyAnalyzer().Analyze(model);

        var tableDependencies = graph.GetDependencies("public.orders");
        Assert.IsFalse(
            tableDependencies.Any(static node => string.Equals(node.NodeId, "public.standalone_seq", StringComparison.OrdinalIgnoreCase)),
            "A sequence without OWNED BY must not be added as a table dependency.");
    }

    [TestMethod]
    public void MultipleOwnedSequencesAllAppearsBeforeTable()
    {
        var model = CreateModel(
            CreateTable("public.invoices", "invoices", "public"),
            CreateSequence("public.invoices_id_seq", "invoices_id_seq", "public", ownedByTable: "public.invoices", ownedByColumn: "id"),
            CreateSequence("public.invoices_number_seq", "invoices_number_seq", "public", ownedByTable: "public.invoices", ownedByColumn: "number"));

        var graph = new DependencyAnalyzer().Analyze(model);
        var order = graph.TopologicalSort().Select(static node => node.NodeId).ToList();

        var tableIndex = order.IndexOf("public.invoices");
        var idSeqIndex = order.IndexOf("public.invoices_id_seq");
        var numberSeqIndex = order.IndexOf("public.invoices_number_seq");

        Assert.IsTrue(idSeqIndex < tableIndex, "invoices_id_seq must appear before invoices.");
        Assert.IsTrue(numberSeqIndex < tableIndex, "invoices_number_seq must appear before invoices.");
    }

    [TestMethod]
    public void UnrelatedTableDoesNotAcquireSequenceDependency()
    {
        var model = CreateModel(
            CreateTable("public.orders", "orders", "public"),
            CreateTable("public.products", "products", "public"),
            CreateSequence("public.orders_id_seq", "orders_id_seq", "public", ownedByTable: "public.orders", ownedByColumn: "id"));

        var graph = new DependencyAnalyzer().Analyze(model);

        var productDependencies = graph.GetDependencies("public.products");
        Assert.IsFalse(
            productDependencies.Any(static node => string.Equals(node.NodeId, "public.orders_id_seq", StringComparison.OrdinalIgnoreCase)),
            "An unrelated table must not acquire the sequence dependency.");
    }

    private static DatabaseModel CreateModel(params ISemanticNode[] nodes)
    {
        var tables = nodes.OfType<TableDefinition>()
            .ToDictionary(static t => t.Key, StringComparer.OrdinalIgnoreCase);
        var sequences = nodes.OfType<SequenceDefinition>()
            .ToDictionary(static s => s.Key, StringComparer.OrdinalIgnoreCase);

        return DatabaseModel.Empty with
        {
            Tables = tables,
            Sequences = sequences,
        };
    }

    private static TableDefinition CreateTable(string key, string name, string? schemaName)
        => new(key, name, schemaName, [], [], [], [], [], [], [], [], [], []);

    private static SequenceDefinition CreateSequence(
        string key,
        string name,
        string? schemaName,
        string? ownedByTable,
        string? ownedByColumn)
        => new(key, name, schemaName, [], [], [], [], [], [], [], [])
        {
            OwnedByTable = ownedByTable,
            OwnedByColumn = ownedByColumn,
        };
}
