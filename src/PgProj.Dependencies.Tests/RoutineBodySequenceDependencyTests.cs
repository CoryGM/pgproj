using PgProj.Semantics;

namespace PgProj.Dependencies.Tests;

/// <summary>G-173: a SQL-language routine body that names a sequence is created after the sequence.</summary>
[TestClass]
public sealed class RoutineBodySequenceDependencyTests
{
    [TestMethod]
    public void SqlFunctionDependsOnTheSequenceItsBodyNames()
    {
        var model = CreateModel(
            CreateFunction("sales.next_order_number", "sales", "sql", "SELECT 'SO-' || nextval('sales.order_number_seq')::text;"),
            CreateSequence("sales.order_number_seq", "order_number_seq", "sales"));

        var graph = new DependencyAnalyzer().Analyze(model);

        Assert.IsTrue(graph.GetDependencies("sales.next_order_number").Any(static node => node.NodeId == "sales.order_number_seq"));
        var order = graph.TopologicalSort().Select(static node => node.NodeId).ToList();
        Assert.IsTrue(order.IndexOf("sales.order_number_seq") < order.IndexOf("sales.next_order_number"));
    }

    [TestMethod]
    public void SqlFunctionDependsOnASequenceNamedWithARegclassCastAndAnUnqualifiedName()
    {
        var model = CreateModel(
            CreateFunction("sales.current_number", "sales", "SQL", "SELECT currval('order_number_seq'::regclass);"),
            CreateSequence("sales.order_number_seq", "order_number_seq", "sales"));

        var graph = new DependencyAnalyzer().Analyze(model);

        Assert.IsTrue(graph.GetDependencies("sales.current_number").Any(static node => node.NodeId == "sales.order_number_seq"));
    }

    [TestMethod]
    public void ThePlpgsqlBodyOfAFunctionIsNotRead()
    {
        var model = CreateModel(
            CreateFunction("sales.next_order_number", "sales", "plpgsql", "BEGIN RETURN nextval('sales.order_number_seq')::text; END;"),
            CreateSequence("sales.order_number_seq", "order_number_seq", "sales"));

        var graph = new DependencyAnalyzer().Analyze(model);

        Assert.IsFalse(graph.GetDependencies("sales.next_order_number").Any(static node => node.NodeId == "sales.order_number_seq"));
    }

    [TestMethod]
    public void ANameThatIsNotAProjectSequenceAddsNoDependency()
    {
        var model = CreateModel(
            CreateFunction("sales.next_order_number", "sales", "sql", "SELECT nextval('other.not_in_project_seq');"),
            CreateSequence("sales.order_number_seq", "order_number_seq", "sales"));

        var graph = new DependencyAnalyzer().Analyze(model);

        Assert.AreEqual(0, graph.GetDependencies("sales.next_order_number").Count());
    }

    [TestMethod]
    public void SqlProcedureDependsOnTheSequenceItsBodyNames()
    {
        var model = CreateModel(
            new ProcedureDefinition("sales.bump", "bump", "sales", "sql", [], [], [], [], [], [], [], [])
            {
                RawBody = "SELECT nextval('sales.order_number_seq');",
            },
            CreateSequence("sales.order_number_seq", "order_number_seq", "sales"));

        var graph = new DependencyAnalyzer().Analyze(model);

        Assert.IsTrue(graph.GetDependencies("sales.bump").Any(static node => node.NodeId == "sales.order_number_seq"));
    }

    private static DatabaseModel CreateModel(params ISemanticNode[] nodes)
        => DatabaseModel.Empty with
        {
            Functions = nodes.OfType<FunctionDefinition>().ToDictionary(static f => f.Key, StringComparer.OrdinalIgnoreCase),
            Procedures = nodes.OfType<ProcedureDefinition>().ToDictionary(static p => p.Key, StringComparer.OrdinalIgnoreCase),
            Sequences = nodes.OfType<SequenceDefinition>().ToDictionary(static s => s.Key, StringComparer.OrdinalIgnoreCase),
        };

    private static FunctionDefinition CreateFunction(string key, string schemaName, string language, string body)
        => new(key, key[(key.IndexOf('.') + 1)..], schemaName, language, [], [], [], [], [], [], [], []) { RawBody = body };

    private static SequenceDefinition CreateSequence(string key, string name, string schemaName)
        => new(key, name, schemaName, [], [], [], [], [], [], [], []);
}
