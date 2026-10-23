using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-243 against a real server. A routine whose body is blank was refused ("no original body was captured"). It is now deployed as written, converges, and is scaffolded.
/// </summary>
[TestClass]
public sealed class BlankRoutineBodyEndToEndTests
{
    [TestMethod]
    public async Task ABlankProcedureAndFunctionBodyIsDeployedConvergesAndIsScaffolded()
    {
        var databaseName = await CreateDatabaseAsync("g243");
        var projectDirectory = CreateProjectDirectory("g243");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g243-init-" + Guid.NewGuid().ToString("N"));
        var secondDatabase = await CreateDatabaseAsync("g243b");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, """
                CREATE PROCEDURE public.nothing(IN integer) LANGUAGE sql AS $procedure$ $procedure$;
                CREATE FUNCTION public.stub() RETURNS void LANGUAGE sql AS $$ $$;
                """);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("2", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_proc WHERE proname IN ('nothing', 'stub')"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var secondConnection = ConnectionStringFor(secondDatabase);
            var republished = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, republished.ExitCode, "publishing the scaffold failed.\n" + republished.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);
        }
        finally
        {
            await DropDatabaseAsync(secondDatabase);
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
