using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-189 against a real server. A view written <c>CREATE VIEW v (x, y) AS ...</c> was deployed without its column list, so its columns kept the query's own
/// names and anything reading <c>x</c> or <c>y</c> failed.
/// </summary>
[TestClass]
public sealed class ViewColumnAliasEndToEndTests
{
    private const string Sql = """
        CREATE TABLE public.e (id int PRIMARY KEY, name text);
        CREATE VIEW public.aliased (x, y) AS SELECT id, name FROM public.e;
        CREATE MATERIALIZED VIEW public.mv (a, b) AS SELECT id, name FROM public.e;
        """;

    [TestMethod]
    public async Task ColumnListsAreDeployed_ConvergeAndAChangeIsSeen()
    {
        var databaseName = await CreateDatabaseAsync("g189");
        var projectDirectory = CreateProjectDirectory("g189");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g189-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("x,y", await ColumnsAsync(connectionString, "aliased"));
            Assert.AreEqual("a,b", await ColumnsAsync(connectionString, "mv"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            // Renaming a column in the list is a change and is deployed (the view is dropped and created again, as for any column rename).
            WriteSchema(projectDirectory, Edit(Sql, "aliased (x, y)", "aliased (x, z)"));
            var diffed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            StringAssert.Contains(diffed.Output, "aliased");
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            Assert.AreEqual("x,z", await ColumnsAsync(connectionString, "aliased"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }

    private static Task<string> ColumnsAsync(string connectionString, string relation)
        => ScalarTextAsync(connectionString, $"SELECT string_agg(attname, ',' ORDER BY attnum) FROM pg_attribute WHERE attrelid = 'public.{relation}'::regclass AND attnum > 0 AND NOT attisdropped");
}
