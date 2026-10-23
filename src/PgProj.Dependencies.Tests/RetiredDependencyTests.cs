using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Dependencies.Tests;

/// <summary>
/// G-52: the merged graph keeps only the dependencies of an object's new definition, so a survivor that stopped using a removed
/// object left no trace of it. The analyzer now records those retired dependencies separately, without letting them affect the
/// graph's edges, its topological order, or cycle detection.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RetiredDependencyTests
{
    private const string Mood = "create type public.mood as enum ('happy', 'sad'); ";

    [TestMethod]
    public void SurvivorThatStoppedUsingARemovedTypeRecordsTheRetiredDependency()
    {
        var graph = Analyze(Mood + "create table public.t (m public.mood);", "create table public.t (m text);");

        CollectionAssert.Contains(graph.GetRetiredDependencies("public.t").ToList(), "public.mood");
        CollectionAssert.Contains(graph.GetRetiredDependents("public.mood").ToList(), "public.t");
    }

    [TestMethod]
    public void RetiredDependencyIsNotAnEdgeOfTheGraph()
    {
        var graph = Analyze(Mood + "create table public.t (m public.mood);", "create table public.t (m text);");

        Assert.IsFalse(graph.Edges.Any(static edge => edge.FromNodeId == "public.t" && edge.ToNodeId == "public.mood"));
        Assert.IsFalse(graph.GetDependencies("public.t").Any(static node => node.NodeId == "public.mood"));
        Assert.IsFalse(graph.GetDependents("public.mood").Any(static node => node.NodeId == "public.t"));
        Assert.IsEmpty(graph.DetectCycles());
    }

    [TestMethod]
    public void RetiredDependencyDoesNotChangeTheTopologicalOrder()
    {
        var withRetired = Analyze(Mood + "create table public.t (m public.mood);", "create table public.t (m text);");
        var without = new DependencyAnalyzer().Analyze(Build("create table public.t (m text);"));

        // The removed type is a node of the merged graph, but nothing new depends on it.
        var order = withRetired.TopologicalSort().Select(static node => node.NodeId).Where(static id => id != "public.mood").ToArray();
        CollectionAssert.AreEqual(without.TopologicalSort().Select(static node => node.NodeId).ToArray(), order);
    }

    [TestMethod]
    public void SurvivorThatStillUsesTheTypeRecordsNothing()
    {
        var graph = Analyze(Mood + "create table public.t (m public.mood, x integer);", Mood + "create table public.t (m public.mood, x bigint);");

        Assert.IsEmpty(graph.GetRetiredDependencies("public.t"));
        Assert.IsEmpty(graph.GetRetiredDependents("public.mood"));
    }

    [TestMethod]
    public void RemovedObjectHasNoRetiredDependenciesOfItsOwn()
    {
        var graph = Analyze(Mood + "create table public.t (m public.mood);", string.Empty);

        // Both were removed: the table's old edge to the type is an ordinary edge of the graph, not a retired one.
        Assert.IsEmpty(graph.GetRetiredDependencies("public.t"));
        Assert.IsTrue(graph.GetDependencies("public.t").Any(static node => node.NodeId == "public.mood"));
    }

    [TestMethod]
    public void AddedObjectHasNoRetiredDependencies()
    {
        var graph = Analyze(string.Empty, Mood + "create table public.t (m public.mood);");

        Assert.IsEmpty(graph.GetRetiredDependencies("public.t"));
    }

    [TestMethod]
    public void DefaultThatStoppedCallingARemovedFunctionIsRetired()
    {
        var graph = Analyze(
            "create function public.z_bump(x integer) returns integer language sql as $$ select x + 1 $$; create table public.a_t (n integer default public.z_bump(1));",
            "create table public.a_t (n integer);");

        Assert.IsTrue(graph.GetRetiredDependents(graph.Nodes.Keys.Single(static key => key.StartsWith("public.z_bump", StringComparison.OrdinalIgnoreCase))).Contains("public.a_t"));
    }

    [TestMethod]
    public void SerialColumnMadePlainRetiresItsSequence()
    {
        var graph = Analyze("create table public.t (id serial primary key);", "create table public.t (id integer primary key);");

        CollectionAssert.Contains(graph.GetRetiredDependents("public.t_id_seq").ToList(), "public.t");
    }

    /// <summary>Every object depends on every extension (G-46), so removing one is retired by every survivor.</summary>
    [TestMethod]
    public void RemovedExtensionIsRetiredBySurvivors()
    {
        var graph = Analyze("create extension citext; create table public.t (c citext);", "create table public.t (c text);");

        CollectionAssert.Contains(graph.GetRetiredDependents("extension:citext").ToList(), "public.t");
    }

    [TestMethod]
    public void SingleModelAnalysisHasNoRetiredDependencies()
    {
        var graph = new DependencyAnalyzer().Analyze(Build(Mood + "create table public.t (m public.mood);"));

        Assert.IsEmpty(graph.GetRetiredDependencies("public.t"));
        Assert.IsEmpty(graph.GetRetiredDependents("public.mood"));
    }

    [TestMethod]
    public void GraphRecordsAndReturnsRetiredDependenciesByExactNodeId()
    {
        var graph = new DependencyGraph();

        // Node ids are keys, compared exactly as PostgreSQL compares names (G-206): public.A and public.a are two objects.
        graph.AddRetiredDependency("public.A", "public.B");
        graph.AddRetiredDependency("public.a", "public.b");
        graph.AddRetiredDependency("public.a", "public.b");

        Assert.HasCount(1, graph.GetRetiredDependencies("public.A"));
        Assert.HasCount(1, graph.GetRetiredDependencies("public.a"));
        Assert.HasCount(1, graph.GetRetiredDependents("public.b"));
        Assert.IsEmpty(graph.GetRetiredDependencies("PUBLIC.A"));
        Assert.IsEmpty(graph.GetRetiredDependencies("public.unknown"));
    }

    private static DependencyGraph Analyze(string oldSql, string newSql)
        => new DependencyAnalyzer().Analyze(Build(oldSql), Build(newSql));

    private static DatabaseModel Build(string sql)
        => string.IsNullOrWhiteSpace(sql) ? DatabaseModel.Empty : new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);
}
