using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Dependencies.Tests;

/// <summary>G-148: a column, index key, or range type that names a project collation is created after it; a built-in collation adds nothing.</summary>
[TestClass]
public sealed class CollationDependencyTests
{
    private const string Collation = "CREATE COLLATION app.ci (provider = icu, locale = 'und-u-ks-level2', deterministic = false); ";

    [TestMethod]
    public void AColumnDependsOnItsCollation()
    {
        var graph = Analyze(Collation + "CREATE TABLE app.t (n text COLLATE app.ci);");

        Assert.IsTrue(graph.GetDependencies("app.t").Any(static node => node.NodeId == "collation:app.ci"));
    }

    [TestMethod]
    public void AnIndexKeyDependsOnItsCollation()
    {
        var graph = Analyze(Collation + "CREATE TABLE app.t (n text); CREATE INDEX t_n ON app.t (n COLLATE app.ci);");

        Assert.IsTrue(graph.GetDependencies("app.t_n").Any(static node => node.NodeId == "collation:app.ci"));
    }

    [TestMethod]
    public void ABuiltInCollationAddsNoEdge()
    {
        var graph = Analyze(Collation + "CREATE TABLE app.t (n text COLLATE \"C\");");

        Assert.IsFalse(graph.GetDependencies("app.t").Any(static node => node.NodeId == "collation:app.ci"));
    }

    private static DependencyGraph Analyze(string sql)
        => new DependencyAnalyzer().Analyze(new SemanticPipeline().Execute([new PgQueryParser().Parse("CREATE SCHEMA app; " + sql)]));
}
