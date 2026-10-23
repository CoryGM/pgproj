using PgProj.Semantics;

namespace PgProj.Dependencies.Tests;

/// <summary>
/// Regression coverage for G-29: with <see cref="FunctionDefinition.Key"/> now overload-qualified
/// (schema.name(argtypes)), bare function references captured from call sites (which do not carry
/// argument types) must still resolve via a name-based fallback in
/// <see cref="DependencyAnalyzer"/>, and distinct overloads must not collide into a single node.
/// </summary>
[TestClass]
public sealed class OverloadedRoutineDependencyTests
{
    [TestMethod]
    public void BareFunctionReferenceResolvesToOverloadQualifiedKeyViaNameFallback()
    {
        var model = CreateModel(
            CreateFunction("public.next_code(integer)", "next_code", "public"),
            CreateTable("public.orders", "orders", "public", referencedFunctions: ["next_code"]));

        var graph = new DependencyAnalyzer().Analyze(model);

        var tableDependencies = graph.GetDependencies("public.orders");
        Assert.IsTrue(
            tableDependencies.Any(static node => string.Equals(node.NodeId, "public.next_code(integer)", StringComparison.OrdinalIgnoreCase)),
            "Expected the bare 'public.next_code' reference to resolve to the overload-qualified function key.");
    }

    [TestMethod]
    public void OverloadedFunctionsProduceDistinctGraphNodes()
    {
        var model = CreateModel(
            CreateFunction("public.calc(integer, integer)", "calc", "public"),
            CreateFunction("public.calc(text, text)", "calc", "public"));

        var graph = new DependencyAnalyzer().Analyze(model);

        Assert.IsTrue(graph.Nodes.ContainsKey("public.calc(integer, integer)"));
        Assert.IsTrue(graph.Nodes.ContainsKey("public.calc(text, text)"));
        Assert.AreEqual(2, graph.Nodes.Count);
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
        IReadOnlyList<string>? referencedFunctions = null)
        => new(key, name, schemaName, [], [], [], [], [], referencedFunctions ?? [], [], [], [], []);

    private static FunctionDefinition CreateFunction(
        string key,
        string name,
        string? schemaName,
        IReadOnlyList<string>? referencedFunctions = null)
        => new(key, name, schemaName, "sql", [], [], [], referencedFunctions ?? [], [], [], [], []);
}
