using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Core.Tests;

/// <summary>
/// G-150: a live statistics object is the server's own <c>pg_get_statisticsobjdef</c> text, which the parser reads back as the same object a project's statement gives, and
/// <c>init</c> writes it in its table's file.
/// </summary>
[TestClass]
public sealed class LiveStatisticsTests
{
    private static readonly LiveTableSnapshot Table = new("app", "t", [new LiveColumnSnapshot("a", "integer", true, null, false, null, null), new LiveColumnSnapshot("b", "integer", true, null, false, null, null)]);

    private static LiveStatisticsSnapshot Statistics() => new("app", "t_ab", "app", "t", "CREATE STATISTICS app.t_ab (dependencies) ON a, b FROM app.t");

    [TestMethod]
    public void ALiveStatisticsObjectIsTheSameAsTheProjectsStatement()
    {
        var sql = LiveDatabaseModelLoader.GenerateSnapshotSql([new LiveSchemaSnapshot("app")], [Table], [], [], statistics: [Statistics()]);
        var live = new LiveDatabaseModelLoader(new PgQueryParser(), new SemanticBuilder()).BuildModelFromSnapshotSql(sql).Statistics["statistics:app.t_ab"];
        var project = new SemanticPipeline().Execute([new PgQueryParser().Parse("CREATE SCHEMA app; CREATE TABLE app.t (a int, b int); CREATE STATISTICS app.t_ab (dependencies) ON a, b FROM app.t;")]).Statistics["statistics:app.t_ab"];

        CollectionAssert.AreEqual(project.NormalizedKinds.ToArray(), live.NormalizedKinds.ToArray());
        CollectionAssert.AreEqual(project.Elements.Select(static e => e.Name).ToArray(), live.Elements.Select(static e => e.Name).ToArray());
        Assert.AreEqual(project.TableName, live.TableName);
    }

    [TestMethod]
    public void InitWritesStatisticsInTheirTablesFile()
    {
        var files = LiveDatabaseModelLoader.BuildScaffoldFiles([new LiveSchemaSnapshot("app")], [Table], [], [], [], [], [], [], [], [], [], statistics: [Statistics()]);

        var tableFile = files.Single(static f => f.Content.Contains("CREATE TABLE", StringComparison.Ordinal));
        StringAssert.Contains(tableFile.Content, "CREATE STATISTICS app.t_ab (dependencies) ON a, b FROM app.t;");
        Assert.AreEqual(1, files.Count(static f => f.Content.Contains("CREATE STATISTICS", StringComparison.Ordinal)));
    }
}
