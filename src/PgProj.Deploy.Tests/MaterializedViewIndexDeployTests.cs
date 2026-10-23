using PgProj.Dependencies;
using PgProj.Diff;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Deploy.Tests;

/// <summary>
/// G-171: the script creates a materialized view's indexes after the view, and a view that is dropped and created again to be changed
/// takes its indexes with it, so each one the project keeps is dropped first and created again after the view in the same script.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class MaterializedViewIndexDeployTests
{
    private const string Prelude = "CREATE SCHEMA s; CREATE TABLE s.t (grp int, amount numeric); ";

    private const string View = "CREATE MATERIALIZED VIEW s.z_mv AS SELECT grp, sum(amount) AS total FROM s.t GROUP BY grp; ";

    private const string Indexes = "CREATE UNIQUE INDEX a_grp ON s.z_mv (grp); CREATE INDEX b_total ON s.z_mv (total DESC);";

    [TestMethod]
    public void TheIndexesOfANewViewAreCreatedAfterTheView()
    {
        var script = Script(Prelude, Prelude + View + Indexes);

        AssertInOrder(script, "CREATE MATERIALIZED VIEW \"s\".\"z_mv\"", "CREATE UNIQUE INDEX \"a_grp\" ON \"s\".\"z_mv\"", "CREATE INDEX \"b_total\" ON \"s\".\"z_mv\"");
    }

    [TestMethod]
    public void ChangingTheViewDropsAndCreatesItsIndexesAroundIt()
    {
        var changedView = View.Replace("sum(amount) AS total", "sum(amount) AS total, count(*) AS n", StringComparison.Ordinal);

        var script = Script(Prelude + View + Indexes, Prelude + changedView + Indexes);

        AssertInOrder(
            script,
            "DROP INDEX \"s\".\"a_grp\"",
            "DROP MATERIALIZED VIEW \"s\".\"z_mv\"",
            "CREATE MATERIALIZED VIEW \"s\".\"z_mv\"",
            "CREATE UNIQUE INDEX \"a_grp\" ON \"s\".\"z_mv\"");
        AssertInOrder(script, "DROP INDEX \"s\".\"b_total\"", "DROP MATERIALIZED VIEW \"s\".\"z_mv\"", "CREATE MATERIALIZED VIEW \"s\".\"z_mv\"", "CREATE INDEX \"b_total\" ON \"s\".\"z_mv\"");
        StringAssert.Contains(script, "is dropped and created again");
    }

    [TestMethod]
    public void ACommentOnlyChangeToTheViewLeavesItsIndexesAlone()
    {
        var script = Script(
            Prelude + View + Indexes + "COMMENT ON MATERIALIZED VIEW s.z_mv IS 'old';",
            Prelude + View + Indexes + "COMMENT ON MATERIALIZED VIEW s.z_mv IS 'new';");

        Assert.DoesNotContain("INDEX", script, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP MATERIALIZED VIEW", script, StringComparison.Ordinal);
    }

    [TestMethod]
    public void AnIndexAddedAtTheSameTimeAsAViewChangeIsCreatedOnce()
    {
        var changedView = View.Replace("sum(amount) AS total", "sum(amount) AS total, count(*) AS n", StringComparison.Ordinal);

        var script = Script(Prelude + View + "CREATE UNIQUE INDEX a_grp ON s.z_mv (grp);", Prelude + changedView + Indexes);

        Assert.AreEqual(1, CountOf(script, "CREATE INDEX \"b_total\""), script);
        Assert.AreEqual(1, CountOf(script, "CREATE UNIQUE INDEX \"a_grp\""), script);
    }

    [TestMethod]
    public void RemovingTheViewDropsItsIndexesFirst()
    {
        var script = Script(Prelude + View + Indexes, Prelude);

        AssertInOrder(script, "DROP INDEX \"s\".\"a_grp\"", "DROP MATERIALIZED VIEW \"s\".\"z_mv\"");
        AssertInOrder(script, "DROP INDEX \"s\".\"b_total\"", "DROP MATERIALIZED VIEW \"s\".\"z_mv\"");
    }

    private static string Script(string oldSql, string newSql)
    {
        var oldModel = Build(oldSql);
        var newModel = Build(newSql);
        var generator = new ScriptGenerator();
        var batch = generator.Generate(new DiffEngine().Diff(oldModel, newModel), new DependencyAnalyzer().Analyze(oldModel, newModel));
        return generator.GenerateSqlScript(batch);
    }

    private static DatabaseModel Build(string sql)
        => new SemanticPipeline().Execute([new PgQueryParser().Parse(sql)]);

    private static void AssertInOrder(string script, params string[] fragments)
    {
        var position = -1;
        foreach (var fragment in fragments)
        {
            var found = script.IndexOf(fragment, position + 1, StringComparison.Ordinal);
            Assert.IsGreaterThan(position, found, $"Expected '{fragment}' after position {position} in:\n{script}");
            position = found;
        }
    }

    private static int CountOf(string text, string fragment)
    {
        var count = 0;
        for (var index = text.IndexOf(fragment, StringComparison.Ordinal); index >= 0; index = text.IndexOf(fragment, index + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
