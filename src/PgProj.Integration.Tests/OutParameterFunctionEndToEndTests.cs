using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-187 against a real server. A function declared with only OUT parameters is read back as <c>RETURNS record</c>, which the project does not state, so it was
/// modified after every publish.
/// </summary>
[TestClass]
public sealed class OutParameterFunctionEndToEndTests
{
    private const string Sql = """
        CREATE FUNCTION public.g(OUT x int, OUT y text) LANGUAGE sql AS $$ SELECT 1, 'a' $$;
        CREATE FUNCTION public.one(OUT x int) LANGUAGE sql AS $$ SELECT 1 $$;
        CREATE FUNCTION public.mixed(a int, INOUT b int, OUT c text) LANGUAGE sql AS $$ SELECT b, 'a' $$;
        """;

    [TestMethod]
    public async Task FunctionsWithOutParametersConverge_AndAChangedOutputIsSeen()
    {
        var databaseName = await CreateDatabaseAsync("g187");
        var projectDirectory = CreateProjectDirectory("g187");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g187-init-" + Guid.NewGuid().ToString("N"));
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

            // A changed output type is a different result: seen as a change.
            WriteSchema(projectDirectory, Edit(Sql, "OUT y text) LANGUAGE sql AS $$ SELECT 1, 'a' $$", "OUT y varchar) LANGUAGE sql AS $$ SELECT 1, 'a' $$"));
            var diffed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            Assert.DoesNotContain("Modified: 0", diffed.Output, StringComparison.Ordinal);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
