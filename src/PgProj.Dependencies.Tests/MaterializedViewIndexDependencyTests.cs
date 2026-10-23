using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Dependencies.Tests;

/// <summary>
/// G-171: an index on a materialized view is created after the view and dropped before it. The index is named so that it sorts before the
/// view (<c>a_idx</c> and <c>z_mv</c>), since an order that merely follows the names would pass without the dependency.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class MaterializedViewIndexDependencyTests
{
    private const string Sql = "CREATE SCHEMA s; CREATE TABLE s.t (grp int); CREATE INDEX a_idx ON s.z_mv (grp); CREATE MATERIALIZED VIEW s.z_mv AS SELECT grp FROM s.t;";

    [TestMethod]
    public void AnIndexOnAMaterializedViewDependsOnTheView()
    {
        var graph = Analyze();

        Assert.IsTrue(graph.GetDependencies("s.a_idx").Any(static node => node.NodeId == "s.z_mv"));
        Assert.IsTrue(graph.GetDependents("s.z_mv").Any(static node => node.NodeId == "s.a_idx"));
    }

    [TestMethod]
    public void TheViewIsCreatedBeforeItsIndex()
    {
        var order = Analyze().TopologicalSort().Select(static node => node.NodeId).ToList();

        Assert.IsTrue(order.IndexOf("s.z_mv") >= 0 && order.IndexOf("s.a_idx") >= 0, string.Join(", ", order));
        Assert.IsLessThan(order.IndexOf("s.a_idx"), order.IndexOf("s.z_mv"), string.Join(", ", order));
    }

    private static DependencyGraph Analyze()
        => new DependencyAnalyzer().Analyze(new SemanticPipeline().Execute([new PgQueryParser().Parse(Sql)]));
}
