using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-299 against a real server, on every version. A child that inherits a generated column and lists the column too (<c>INHERITS (parent)</c>, as <c>pg_dump</c> writes it before
/// PostgreSQL 16) was scaffolded with the generation expression repeated, which 14 and 15 refuse (42611), so the scaffold did not publish.
/// </summary>
[TestClass]
public sealed class InheritedGeneratedColumnEndToEndTests
{
    private const string Schema = """
        CREATE TABLE public.gparent (a int, b int GENERATED ALWAYS AS (a * 2) STORED);
        CREATE TABLE public.gchild (a int, b int) INHERITS (public.gparent);
        """;

    private const string Query = """
        SELECT string_agg(c.relname || '.' || a.attname || ':' || a.attgenerated::text, '|' ORDER BY c.relname, a.attname)
        FROM pg_class c JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
        WHERE c.relname IN ('gparent', 'gchild') AND c.relnamespace = 'public'::regnamespace;
        """;

    [TestMethod]
    public async Task AChildOfAGeneratedColumnPublishesRoundTripsThroughInitAndConverges()
    {
        var databaseName = await CreateDatabaseAsync("g299");
        var scaffoldDatabaseName = await CreateDatabaseAsync("g299s");
        var projectDirectory = CreateProjectDirectory("g299");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Schema);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            var generated = await ScalarTextAsync(connectionString, Query);
            StringAssert.Contains(generated, "gparent.b:s");
            StringAssert.Contains(generated, "gchild.b:s", "the child's column is generated too, from its parent");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldDirectory = Path.Combine(projectDirectory, "scaffold");
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--source-connection", connectionString, "--output", scaffoldDirectory, "--project", scaffoldProject);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            var scaffoldConnectionString = ConnectionStringFor(scaffoldDatabaseName);
            var deployedScaffold = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", scaffoldConnectionString, "--execute");
            Assert.AreEqual(0, deployedScaffold.ExitCode, "the scaffold did not publish.\n" + deployedScaffold.Output);
            Assert.AreEqual(generated, await ScalarTextAsync(scaffoldConnectionString, Query));
            await AssertDiffIsEmptyAsync(scaffoldProject, scaffoldConnectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await DropDatabaseAsync(scaffoldDatabaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
