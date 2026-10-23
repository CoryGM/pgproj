using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-232 against a real server. Two procedures that differ only by an <c>OUT</c> parameter both exist from PostgreSQL 14, and an <c>ALTER</c>, <c>COMMENT</c>, or
/// <c>GRANT</c> that named one by its types alone failed with "procedure name is not unique". They are now named with every parameter's mode.
/// </summary>
[TestClass]
public sealed class ProcedureOutOverloadEndToEndTests
{
    [TestMethod]
    public async Task TwoProceduresThatDifferByAnOutParameterArePublishedCommentedGrantedAndScaffolded()
    {
        var databaseName = await CreateDatabaseAsync("g232");
        var projectDirectory = CreateProjectDirectory("g232");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g232-init-" + Guid.NewGuid().ToString("N"));
        var secondDatabase = await CreateDatabaseAsync("g232b");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, """
                CREATE PROCEDURE public.ptest10(IN a int, IN b int, IN c int) LANGUAGE sql AS 'select 1';
                CREATE PROCEDURE public.ptest10(OUT a int, IN b int, IN c int) LANGUAGE sql AS 'select 2';
                ALTER PROCEDURE public.ptest10(IN a int, IN b int, IN c int) OWNER TO postgres;
                ALTER PROCEDURE public.ptest10(OUT a int, IN b int, IN c int) OWNER TO postgres;
                COMMENT ON PROCEDURE public.ptest10(IN a int, IN b int, IN c int) IS 'in only';
                COMMENT ON PROCEDURE public.ptest10(OUT a int, IN b int, IN c int) IS 'with out';
                GRANT EXECUTE ON PROCEDURE public.ptest10(IN a int, IN b int, IN c int) TO PUBLIC;
                """);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            const string comments = "SELECT string_agg(oid::regprocedure::text || '=' || obj_description(oid, 'pg_proc'), ',' ORDER BY oid::regprocedure::text) FROM pg_proc WHERE proname = 'ptest10'";
            Assert.AreEqual("ptest10(integer,integer)=with out,ptest10(integer,integer,integer)=in only", await ScalarTextAsync(connectionString, comments));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var secondConnection = ConnectionStringFor(secondDatabase);
            var republished = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, republished.ExitCode, "publishing the scaffold failed.\n" + republished.Output);
            Assert.AreEqual(await ScalarTextAsync(connectionString, comments), await ScalarTextAsync(secondConnection, comments));
            await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);

            // Dropping one names it unambiguously too.
            WriteSchema(projectDirectory, """
                CREATE PROCEDURE public.ptest10(IN a int, IN b int, IN c int) LANGUAGE sql AS 'select 1';
                """);
            var dropped = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, dropped.ExitCode, "dropping one overload failed.\n" + dropped.Output);
            Assert.AreEqual("1", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_proc WHERE proname = 'ptest10'"));
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
