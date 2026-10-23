using PgProj.Semantics;

namespace PgProj.Dependencies.Tests;

/// <summary>
/// Regression coverage for G-08: wiring already-populated <c>ReferencedTables</c>/
/// <c>ReferencedFunctions</c> reference data into <see cref="DependencyAnalyzer"/> for node
/// kinds that previously ignored it (function, table, constraint, index), plus confirmation
/// that trigger dependency edges already worked via the generic default case.
/// </summary>
[TestClass]
public sealed class DeepenedDependencyGraphTests
{
    [TestMethod]
    public void FunctionDependsOnTableReferencedInBody()
    {
        var model = CreateModel(
            CreateTable("public.orders", "orders", "public"),
            CreateFunction("public.get_order_count", "get_order_count", "public", referencedTables: ["public.orders"]));

        var graph = new DependencyAnalyzer().Analyze(model);

        var functionDependencies = graph.GetDependencies("public.get_order_count");
        Assert.IsTrue(
            functionDependencies.Any(static node => string.Equals(node.NodeId, "public.orders", StringComparison.OrdinalIgnoreCase)),
            "Expected public.get_order_count to depend on public.orders via its body reference.");
    }

    [TestMethod]
    public void TableDependsOnFunctionReferencedInColumnDefault()
    {
        var model = CreateModel(
            CreateFunction("public.next_code", "next_code", "public"),
            CreateTable("public.orders", "orders", "public", referencedFunctions: ["public.next_code"]));

        var graph = new DependencyAnalyzer().Analyze(model);

        var tableDependencies = graph.GetDependencies("public.orders");
        Assert.IsTrue(
            tableDependencies.Any(static node => string.Equals(node.NodeId, "public.next_code", StringComparison.OrdinalIgnoreCase)),
            "Expected public.orders to depend on public.next_code via a column DEFAULT expression.");
    }

    [TestMethod]
    public void TableDependsOnAnotherTableReferencedInColumnDefault()
    {
        var model = CreateModel(
            CreateTable("public.customers", "customers", "public"),
            CreateTable("public.orders", "orders", "public", referencedTables: ["public.customers"]));

        var graph = new DependencyAnalyzer().Analyze(model);

        var tableDependencies = graph.GetDependencies("public.orders");
        Assert.IsTrue(
            tableDependencies.Any(static node => string.Equals(node.NodeId, "public.customers", StringComparison.OrdinalIgnoreCase)),
            "Expected public.orders to depend on public.customers via a column default reference.");
    }

    [TestMethod]
    public void ConstraintDependsOnFunctionReferencedInCheckExpression()
    {
        var model = CreateModel(
            CreateTable("public.users", "users", "public"),
            CreateFunction("public.is_valid_email", "is_valid_email", "public"),
            CreateConstraint(
                "public.users.chk_email",
                "chk_email",
                "public",
                tableName: "public.users",
                referencedFunctions: ["public.is_valid_email"]));

        var graph = new DependencyAnalyzer().Analyze(model);

        var constraintDependencies = graph.GetDependencies("public.users.chk_email");
        Assert.IsTrue(
            constraintDependencies.Any(static node => string.Equals(node.NodeId, "public.is_valid_email", StringComparison.OrdinalIgnoreCase)),
            "Expected the CHECK constraint to depend on the function it calls.");
    }

    [TestMethod]
    public void IndexDependsOnFunctionReferencedInPartialPredicate()
    {
        var model = CreateModel(
            CreateTable("public.orders", "orders", "public"),
            CreateFunction("public.is_active", "is_active", "public"),
            CreateIndex(
                "public.ix_orders_active",
                "ix_orders_active",
                "public",
                tableName: "public.orders",
                referencedFunctions: ["public.is_active"]));

        var graph = new DependencyAnalyzer().Analyze(model);

        var indexDependencies = graph.GetDependencies("public.ix_orders_active");
        Assert.IsTrue(
            indexDependencies.Any(static node => string.Equals(node.NodeId, "public.is_active", StringComparison.OrdinalIgnoreCase)),
            "Expected the partial index to depend on the function used in its predicate.");
    }

    [TestMethod]
    public void TriggerDependsOnItsTargetTable()
    {
        var model = CreateModel(
            CreateTable("public.orders", "orders", "public"),
            CreateFunction("public.audit_order", "audit_order", "public"),
            CreateTrigger("public.trg_orders_audit", "trg_orders_audit", "public", tableName: "public.orders", functionName: "public.audit_order"));

        var graph = new DependencyAnalyzer().Analyze(model);

        var triggerDependencies = graph.GetDependencies("public.trg_orders_audit");
        Assert.IsTrue(
            triggerDependencies.Any(static node => string.Equals(node.NodeId, "public.orders", StringComparison.OrdinalIgnoreCase)),
            "Expected the trigger to depend on its target table.");
    }

    [TestMethod]
    public void TriggerDependsOnItsExecutedFunction()
    {
        var model = CreateModel(
            CreateTable("public.orders", "orders", "public"),
            CreateFunction("public.audit_order", "audit_order", "public"),
            CreateTrigger("public.trg_orders_audit", "trg_orders_audit", "public", tableName: "public.orders", functionName: "public.audit_order"));

        var graph = new DependencyAnalyzer().Analyze(model);

        var triggerDependencies = graph.GetDependencies("public.trg_orders_audit");
        Assert.IsTrue(
            triggerDependencies.Any(static node => string.Equals(node.NodeId, "public.audit_order", StringComparison.OrdinalIgnoreCase)),
            "Expected the trigger to depend on the function it executes.");
    }

    [TestMethod]
    public void TriggerAppearsAfterBothTargetTableAndFunctionInTopologicalOrder()
    {
        var model = CreateModel(
            CreateTable("public.orders", "orders", "public"),
            CreateFunction("public.audit_order", "audit_order", "public"),
            CreateTrigger("public.trg_orders_audit", "trg_orders_audit", "public", tableName: "public.orders", functionName: "public.audit_order"));

        var graph = new DependencyAnalyzer().Analyze(model);
        var order = graph.TopologicalSort().Select(static node => node.NodeId).ToList();

        var tableIndex = order.IndexOf("public.orders");
        var functionIndex = order.IndexOf("public.audit_order");
        var triggerIndex = order.IndexOf("public.trg_orders_audit");

        Assert.IsTrue(tableIndex < triggerIndex, "Table must be created before the trigger.");
        Assert.IsTrue(functionIndex < triggerIndex, "Function must be created before the trigger.");
    }

    [TestMethod]
    public void CircularTableFunctionDependencyIsDetectedAsCycle()
    {
        // A table whose column DEFAULT calls a function, and that same function's body
        // references the table back -- a genuine cycle that the new edges must still surface
        // via the existing GraphCycleException behavior rather than silently dropping an edge.
        var model = CreateModel(
            CreateTable("public.orders", "orders", "public", referencedFunctions: ["public.next_code"]),
            CreateFunction("public.next_code", "next_code", "public", referencedTables: ["public.orders"]));

        Assert.ThrowsExactly<GraphCycleException>(() => new DependencyAnalyzer().Analyze(model));
    }

    private static DatabaseModel CreateModel(params ISemanticNode[] nodes)
    {
        var tables = nodes.OfType<TableDefinition>()
            .ToDictionary(static t => t.Key, StringComparer.OrdinalIgnoreCase);
        var functions = nodes.OfType<FunctionDefinition>()
            .ToDictionary(static f => f.Key, StringComparer.OrdinalIgnoreCase);
        var constraints = nodes.OfType<ConstraintDefinition>()
            .ToDictionary(static c => c.Key, StringComparer.OrdinalIgnoreCase);
        var indexes = nodes.OfType<IndexDefinition>()
            .ToDictionary(static i => i.Key, StringComparer.OrdinalIgnoreCase);
        var triggers = nodes.OfType<TriggerDefinition>()
            .ToDictionary(static t => t.Key, StringComparer.OrdinalIgnoreCase);

        return DatabaseModel.Empty with
        {
            Tables = tables,
            Functions = functions,
            Constraints = constraints,
            Indexes = indexes,
            Triggers = triggers,
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

    private static ConstraintDefinition CreateConstraint(
        string key,
        string name,
        string? schemaName,
        string? tableName,
        IReadOnlyList<string>? referencedFunctions = null)
        => new(key, name, schemaName, tableName, "Check", [], [], null, [], null, [], [], [], referencedFunctions ?? [], [], [], [], []);

    private static IndexDefinition CreateIndex(
        string key,
        string name,
        string? schemaName,
        string? tableName,
        IReadOnlyList<string>? referencedFunctions = null)
        => new(key, name, schemaName, tableName, null, [], [], [], referencedFunctions ?? [], [], [], [], []);

    private static TriggerDefinition CreateTrigger(
        string key,
        string name,
        string? schemaName,
        string? tableName,
        string? functionName)
        => new(key, name, schemaName, tableName)
        {
            FunctionName = functionName,
            ReferencedTables = string.IsNullOrWhiteSpace(tableName) ? [] : [tableName],
            ReferencedFunctions = string.IsNullOrWhiteSpace(functionName) ? [] : [functionName],
        };
}
