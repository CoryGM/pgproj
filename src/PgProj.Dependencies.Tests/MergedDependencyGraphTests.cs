using PgProj.Semantics;

namespace PgProj.Dependencies.Tests;

/// <summary>
/// Regression coverage for G-27: <see cref="DependencyAnalyzer.Analyze(DatabaseModel, DatabaseModel)"/>
/// must produce a single graph containing the union of nodes from both the old (live) and new
/// (source) models, so removed objects participate in dependency-safe ordering instead of being
/// silently absent from the graph.
/// </summary>
[TestClass]
public sealed class MergedDependencyGraphTests
{
    [TestMethod]
    public void RemovedTableOnlyPresentInOldModelIsIncludedInMergedGraph()
    {
        var oldModel = CreateModel(CreateTable("public.legacy_orders", "legacy_orders", "public"));
        var newModel = CreateModel(CreateTable("public.orders", "orders", "public"));

        var graph = new DependencyAnalyzer().Analyze(oldModel, newModel);

        Assert.IsTrue(graph.Nodes.ContainsKey("public.legacy_orders"),
            "A table removed from the new model must still appear in the merged graph.");
        Assert.IsTrue(graph.Nodes.ContainsKey("public.orders"));
    }

    [TestMethod]
    public void RemovedFunctionDependencyOnRemovedTableIsPreservedInMergedGraph()
    {
        var oldModel = CreateModel(
            CreateTable("public.legacy_orders", "legacy_orders", "public"),
            CreateFunction(
                "public.legacy_get_order_count",
                "legacy_get_order_count",
                "public",
                referencedTables: ["public.legacy_orders"]));
        var newModel = CreateModel();

        var graph = new DependencyAnalyzer().Analyze(oldModel, newModel);

        var dependencies = graph.GetDependencies("public.legacy_get_order_count");
        Assert.IsTrue(
            dependencies.Any(node => string.Equals(node.NodeId, "public.legacy_orders", StringComparison.OrdinalIgnoreCase)),
            "The removed function's dependency on the removed table must survive into the merged graph.");

        var orderedNodeIds = graph.TopologicalSort().Select(node => node.NodeId).ToArray();
        var functionIndex = Array.FindIndex(orderedNodeIds, id => string.Equals(id, "public.legacy_get_order_count", StringComparison.OrdinalIgnoreCase));
        var tableIndex = Array.FindIndex(orderedNodeIds, id => string.Equals(id, "public.legacy_orders", StringComparison.OrdinalIgnoreCase));

        Assert.IsTrue(tableIndex < functionIndex,
            "Dependency-first order must place the table before the function that depends on it, so a reversed (drop) order drops the function first.");
    }

    private static DatabaseModel CreateModel(params ISemanticNode[] nodes)
    {
        var tables = nodes.OfType<TableDefinition>()
            .ToDictionary(static t => t.Key, StringComparer.OrdinalIgnoreCase);
        var functions = nodes.OfType<FunctionDefinition>()
            .ToDictionary(static f => f.Key, StringComparer.OrdinalIgnoreCase);

        return DatabaseModel.Empty with
        {
            Tables = tables,
            Functions = functions,
        };
    }

    private static TableDefinition CreateTable(
        string key,
        string name,
        string? schemaName,
        IReadOnlyList<string>? referencedTables = null,
        IReadOnlyList<string>? referencedFunctions = null)
        => new(key, name, schemaName, [], [], referencedTables ?? [], [], [], referencedFunctions ?? [], [], [], [], []);

    private static FunctionDefinition CreateFunction(
        string key,
        string name,
        string? schemaName,
        IReadOnlyList<string>? referencedTables = null,
        IReadOnlyList<string>? referencedFunctions = null)
        => new(key, name, schemaName, "sql", referencedTables ?? [], [], [], referencedFunctions ?? [], [], [], [], []);
}
