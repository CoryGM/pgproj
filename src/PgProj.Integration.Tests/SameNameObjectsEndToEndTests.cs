using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-228 against a real server. A table and a function, statistics object, collation, or text search object of the same name made <c>publish</c> crash with
/// "An item with the same key has already been added". They are now deployed together, converge, and are scaffolded.
/// </summary>
[TestClass]
public sealed class SameNameObjectsEndToEndTests
{
    [TestMethod]
    public async Task ObjectsOfDifferentKindsWithOneNameAreDeployedAndConverge()
    {
        var databaseName = await CreateDatabaseAsync("g228");
        var projectDirectory = CreateProjectDirectory("g228");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g228-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, """
                CREATE TABLE public.t (a int, b int);
                CREATE STATISTICS public.t ON a, b FROM public.t;
                CREATE FUNCTION public.t() RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;
                CREATE COLLATION public.t (provider = icu, locale = 'und');
                CREATE TEXT SEARCH DICTIONARY public.t (TEMPLATE = simple);
                CREATE TEXT SEARCH CONFIGURATION public.t (COPY = english);
                ALTER TEXT SEARCH CONFIGURATION public.t ALTER MAPPING FOR word WITH public.t;
                COMMENT ON FUNCTION public.t() IS 'f';
                COMMENT ON STATISTICS public.t IS 's';
                """);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("1 1 1 1 1", await ScalarTextAsync(connectionString, """
                SELECT (SELECT count(*) FROM pg_proc WHERE proname = 't') || ' ' || (SELECT count(*) FROM pg_statistic_ext WHERE stxname = 't') || ' '
                    || (SELECT count(*) FROM pg_collation WHERE collname = 't') || ' ' || (SELECT count(*) FROM pg_ts_dict WHERE dictname = 't') || ' '
                    || (SELECT count(*) FROM pg_ts_config WHERE cfgname = 't');
                """));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
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
