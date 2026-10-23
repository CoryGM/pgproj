using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-186 against a real server. An index on <c>to_tsvector('english', v)</c> is stored as <c>to_tsvector('english'::regconfig, v)</c>, so it was reported as
/// modified after every publish.
/// </summary>
[TestClass]
public sealed class TextSearchIndexExpressionEndToEndTests
{
    private const string Sql = """
        CREATE TABLE public.w (id int PRIMARY KEY, v text, t text);
        CREATE INDEX w_gin ON public.w USING gin (to_tsvector('english', v));
        CREATE INDEX w_two ON public.w USING gin ((to_tsvector('simple', v) || to_tsvector('simple', t)));
        """;

    [TestMethod]
    public async Task ATextSearchIndexConverges_AndAnotherConfigurationIsStillSeen()
    {
        var databaseName = await CreateDatabaseAsync("g186");
        var projectDirectory = CreateProjectDirectory("g186");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g186-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            WriteSchema(projectDirectory, Edit(Sql, "to_tsvector('english', v)", "to_tsvector('german', v)"));
            var diffed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            StringAssert.Contains(diffed.Output, "w_gin");
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            StringAssert.Contains(await ScalarTextAsync(connectionString, "SELECT indexdef FROM pg_indexes WHERE indexname = 'w_gin'"), "german");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
