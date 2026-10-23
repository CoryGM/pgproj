using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-211 against a real server. A routine in a language other than <c>sql</c>, <c>plpgsql</c>, or <c>internal</c> was a build error, so a project
/// could not declare a C function and a database with one scaffolded into a project that did not build. Any language is now accepted. This uses
/// <c>LANGUAGE c</c>, which every server has; a procedural language (PL/Perl, PL/Python) needs its library on the server, which the test image
/// does not install, and was verified by hand against PostgreSQL 18 with PL/Perl.
/// </summary>
[TestClass]
public sealed class RoutineLanguageEndToEndTests
{
    private const string Sql = """
        CREATE FUNCTION public.my_handler() RETURNS language_handler LANGUAGE c AS '$libdir/plpgsql', 'plpgsql_call_handler';
        CREATE FUNCTION public.abs_of(int) RETURNS int LANGUAGE internal IMMUTABLE STRICT AS 'int4abs';
        """;

    [TestMethod]
    public async Task ACFunctionIsDeployedAndConverges_AndItsScaffoldBuilds()
    {
        var databaseName = await CreateDatabaseAsync("g211");
        var projectDirectory = CreateProjectDirectory("g211");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g211-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Sql);

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-superuser-statements");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("c", await ScalarTextAsync(connectionString, "SELECT l.lanname FROM pg_proc p JOIN pg_language l ON l.oid = p.prolang WHERE p.proname = 'my_handler';"));
            Assert.AreEqual("5", await ScalarTextAsync(connectionString, "SELECT public.abs_of(-5);"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var scaffoldBuilt = await RunAsync("build", "--project", scaffoldProject);
            Assert.AreEqual(0, scaffoldBuilt.ExitCode, scaffoldBuilt.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
