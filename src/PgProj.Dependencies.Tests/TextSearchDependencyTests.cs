using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Dependencies.Tests;

/// <summary>
/// G-149: a configuration is created after the dictionaries it maps and the configuration it copies; an index, column, constraint, or view that names a configuration by a
/// string (<c>to_tsvector('app.cfg', ...)</c>) is created after it; a built-in name adds nothing.
/// </summary>
[TestClass]
public sealed class TextSearchDependencyTests
{
    private const string Setup =
        "CREATE SCHEMA app; "
        + "CREATE TEXT SEARCH DICTIONARY app.syn (TEMPLATE = simple); "
        + "CREATE TEXT SEARCH CONFIGURATION app.cfg (PARSER = pg_catalog.\"default\"); "
        + "ALTER TEXT SEARCH CONFIGURATION app.cfg ADD MAPPING FOR word WITH app.syn, simple; "
        + "CREATE TABLE app.t (body text); ";

    [TestMethod]
    public void AConfigurationDependsOnTheDictionariesItMapsButNotOnABuiltInOne()
    {
        var graph = Analyze(Setup);

        var dependencies = graph.GetDependencies("tsconfiguration:app.cfg").Select(static node => node.NodeId).ToArray();
        CollectionAssert.Contains(dependencies, "tsdictionary:app.syn");
        Assert.AreEqual(1, dependencies.Length);
    }

    [TestMethod]
    public void AConfigurationDependsOnTheConfigurationItCopies()
    {
        var graph = Analyze(Setup + "CREATE TEXT SEARCH CONFIGURATION app.child (COPY = cfg);");

        CollectionAssert.Contains(graph.GetDependencies("tsconfiguration:app.child").Select(static node => node.NodeId).ToArray(), "tsconfiguration:app.cfg");
    }

    [TestMethod]
    public void AnIndexOnAToTsvectorStringDependsOnTheConfiguration()
    {
        var graph = Analyze(Setup + "CREATE INDEX t_fts ON app.t USING gin (to_tsvector('app.cfg'::regconfig, body));");

        CollectionAssert.Contains(graph.GetDependencies("app.t_fts").Select(static node => node.NodeId).ToArray(), "tsconfiguration:app.cfg");
    }

    [TestMethod]
    public void AGeneratedColumnAndAViewThatNameTheConfigurationDependOnIt()
    {
        var graph = Analyze(
            Setup.Replace("body text", "body text, doc tsvector GENERATED ALWAYS AS (to_tsvector('app.cfg', body)) STORED", StringComparison.Ordinal)
            + "CREATE VIEW app.v AS SELECT to_tsvector('app.cfg', body) AS d FROM app.t;");

        CollectionAssert.Contains(graph.GetDependencies("app.t").Select(static node => node.NodeId).ToArray(), "tsconfiguration:app.cfg");
        CollectionAssert.Contains(graph.GetDependencies("app.v").Select(static node => node.NodeId).ToArray(), "tsconfiguration:app.cfg");
    }

    [TestMethod]
    public void ABuiltInConfigurationNamedInAnIndexAddsNoEdge()
    {
        var graph = Analyze(Setup + "CREATE INDEX t_fts ON app.t USING gin (to_tsvector('english', body));");

        Assert.IsFalse(graph.GetDependencies("app.t_fts").Any(static node => node.NodeId == "tsconfiguration:app.cfg"));
    }

    private static DependencyGraph Analyze(string sql)
        => new DependencyAnalyzer().Analyze(new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]));
}
