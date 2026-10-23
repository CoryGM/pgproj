using PgProj.Semantics;

namespace PgProj.Dependencies.Tests;

/// <summary>G-175: a SQL procedure that calls another project procedure is created after it.</summary>
[TestClass]
public sealed class ProcedureCallDependencyTests
{
    [TestMethod]
    public void ASqlProcedureDependsOnTheProcedureItCalls()
    {
        var model = CreateModel(
            CreateProcedure("s.caller()", "sql", "s.target"),
            CreateProcedure("s.target()", "sql"));

        var graph = new DependencyAnalyzer().Analyze(model);

        Assert.IsTrue(graph.GetDependencies("s.caller()").Any(static node => node.NodeId == "s.target()"));
        var order = graph.TopologicalSort().Select(static node => node.NodeId).ToList();
        Assert.IsTrue(order.IndexOf("s.target()") < order.IndexOf("s.caller()"));
    }

    [TestMethod]
    public void AnUnqualifiedCallIsFoundInTheCallersSchema()
    {
        var model = CreateModel(
            CreateProcedure("s.caller()", "sql", "target"),
            CreateProcedure("s.target()", "sql"),
            CreateProcedure("other.target()", "sql"));

        var graph = new DependencyAnalyzer().Analyze(model);

        CollectionAssert.AreEqual(new[] { "s.target()" }, graph.GetDependencies("s.caller()").Select(static node => node.NodeId).ToArray());
    }

    [TestMethod]
    public void AProcedureThatCallsItselfIsNotACycle()
    {
        var model = CreateModel(CreateProcedure("s.again()", "sql", "s.again"));

        var graph = new DependencyAnalyzer().Analyze(model);

        Assert.AreEqual(0, graph.GetDependencies("s.again()").Count());
    }

    [TestMethod]
    public void TwoPlpgsqlProceduresThatCallEachOtherAreNotACycleAndNeedNoOrder()
    {
        var model = CreateModel(
            CreateProcedure("s.ping()", "plpgsql", "s.pong"),
            CreateProcedure("s.pong()", "plpgsql", "s.ping"));

        var graph = new DependencyAnalyzer().Analyze(model);

        Assert.AreEqual(0, graph.GetDependencies("s.ping()").Count());
        Assert.AreEqual(0, graph.GetDependencies("s.pong()").Count());
    }

    [TestMethod]
    public void AChainOfCallsIsOrderedFromTheEndOfTheChain()
    {
        var model = CreateModel(
            CreateProcedure("s.a()", "sql", "s.b"),
            CreateProcedure("s.b()", "sql", "s.c"),
            CreateProcedure("s.c()", "sql"));

        var order = new DependencyAnalyzer().Analyze(model).TopologicalSort().Select(static node => node.NodeId).ToList();

        Assert.IsTrue(order.IndexOf("s.c()") < order.IndexOf("s.b()"));
        Assert.IsTrue(order.IndexOf("s.b()") < order.IndexOf("s.a()"));
    }

    private static DatabaseModel CreateModel(params ProcedureDefinition[] procedures)
        => DatabaseModel.Empty with
        {
            Procedures = procedures.ToDictionary(static p => p.Key, StringComparer.OrdinalIgnoreCase),
        };

    private static ProcedureDefinition CreateProcedure(string key, string language, params string[] calls)
    {
        var name = key[(key.IndexOf('.') + 1)..key.IndexOf('(')];
        return new ProcedureDefinition(key, name, key[..key.IndexOf('.')], language, [], [], [], calls, [], [], [], []);
    }
}
