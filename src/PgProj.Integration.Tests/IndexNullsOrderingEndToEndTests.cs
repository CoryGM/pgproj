using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-185 against a real server. An index key written <c>DESC NULLS FIRST</c> (the default for <c>DESC</c>) is read back as <c>score DESC</c>, so
/// the index and its table were reported as modified after every publish.
/// </summary>
[TestClass]
public sealed class IndexNullsOrderingEndToEndTests
{
    private const string Sql = """
        CREATE TABLE public.p (id int, email text, score int);
        CREATE INDEX p_score ON public.p (score DESC NULLS FIRST, id);
        CREATE INDEX p_email ON public.p (email ASC NULLS LAST, id DESC NULLS LAST);
        CREATE INDEX p_other ON public.p (score ASC NULLS FIRST);
        """;

    [TestMethod]
    public async Task DefaultNullOrderingConverges_AndTheOtherOrderingIsStillSeen()
    {
        var databaseName = await CreateDatabaseAsync("g185");
        var projectDirectory = CreateProjectDirectory("g185");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g185-init-" + Guid.NewGuid().ToString("N"));
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

            // The non-default ordering is a change and is deployed.
            WriteSchema(projectDirectory, Edit(Sql, "(score DESC NULLS FIRST, id)", "(score DESC NULLS LAST, id)"));
            var diffed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            StringAssert.Contains(diffed.Output, "p_score");
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            StringAssert.Contains(await ScalarTextAsync(connectionString, "SELECT indexdef FROM pg_indexes WHERE indexname = 'p_score'"), "DESC NULLS LAST");
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
