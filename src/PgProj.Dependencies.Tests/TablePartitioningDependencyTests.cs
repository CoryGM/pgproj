using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Dependencies.Tests;

/// <summary>
/// G-24c (Phase 2): <c>CREATE TABLE child PARTITION OF parent ...</c> requires the parent to already be a partitioned table, so
/// a partition depends on its parent. This needs no change to <see cref="DependencyAnalyzer"/> itself: a partition's parent key
/// is added to <c>TableDefinition.ReferencedTables</c>, the same generic list every other kind's edges already come from.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class TablePartitioningDependencyTests
{
    [TestMethod]
    public void APartitionDependsOnItsParent()
    {
        var graph = new DependencyAnalyzer().Analyze(Build(
            "CREATE TABLE public.p (id integer, d date) PARTITION BY RANGE (d);"
            + " CREATE TABLE public.p_2024 PARTITION OF public.p FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');"));

        var dependencies = graph.GetDependencies("public.p_2024");
        Assert.IsTrue(
            dependencies.Any(static node => string.Equals(node.NodeId, "public.p", StringComparison.OrdinalIgnoreCase)),
            "Expected public.p_2024 to depend on public.p.");
    }

    [TestMethod]
    public void AParentAppearsBeforeItsPartitionsInTopologicalOrder()
    {
        var graph = new DependencyAnalyzer().Analyze(Build(
            "CREATE TABLE public.p (id integer, d date) PARTITION BY RANGE (d);"
            + " CREATE TABLE public.p_2024 PARTITION OF public.p FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');"
            + " CREATE TABLE public.p_default PARTITION OF public.p DEFAULT;"));

        var order = graph.TopologicalSort().Select(static node => node.NodeId).ToList();
        var parentIndex = order.IndexOf("public.p");
        Assert.IsGreaterThanOrEqualTo(0, parentIndex, "Parent must be present in topological order.");
        Assert.IsLessThan(order.IndexOf("public.p_2024"), parentIndex, string.Join(",", order));
        Assert.IsLessThan(order.IndexOf("public.p_default"), parentIndex, string.Join(",", order));
    }

    [TestMethod]
    public void APartitionsOwnSelfReferenceIsNotAnEdge()
    {
        // Every table's own inline columns add its own name as a (harmless, pre-existing, self-filtered) reference; this
        // confirms a partition's real edge to its parent is not confused with, or lost alongside, that unrelated artifact.
        var graph = new DependencyAnalyzer().Analyze(Build(
            "CREATE TABLE public.p (id integer) PARTITION BY RANGE (id);"
            + " CREATE TABLE public.p1 PARTITION OF public.p FOR VALUES FROM (1) TO (100);"));

        Assert.IsFalse(graph.GetDependencies("public.p1").Any(static node => string.Equals(node.NodeId, "public.p1", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(graph.GetDependencies("public.p").Any(static node => string.Equals(node.NodeId, "public.p", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void AMultiLevelPartitionHierarchyOrdersEachLevelBeforeTheNext()
    {
        var graph = new DependencyAnalyzer().Analyze(Build(
            "CREATE TABLE public.p (id integer, d date) PARTITION BY RANGE (d);"
            + " CREATE TABLE public.p_2024 PARTITION OF public.p FOR VALUES FROM ('2024-01-01') TO ('2025-01-01') PARTITION BY RANGE (d);"
            + " CREATE TABLE public.p_2024_q1 PARTITION OF public.p_2024 FOR VALUES FROM ('2024-01-01') TO ('2024-04-01');"));

        var order = graph.TopologicalSort().Select(static node => node.NodeId).ToList();
        Assert.IsLessThan(order.IndexOf("public.p_2024"), order.IndexOf("public.p"), string.Join(",", order));
        Assert.IsLessThan(order.IndexOf("public.p_2024_q1"), order.IndexOf("public.p_2024"), string.Join(",", order));
    }

    [TestMethod]
    public void AnOrdinaryTableHasNoPartitionDependency()
    {
        var graph = new DependencyAnalyzer().Analyze(Build("CREATE TABLE public.t (a integer);"));

        Assert.IsEmpty(graph.GetDependencies("public.t").ToArray());
    }

    private static DatabaseModel Build(string sql) => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
