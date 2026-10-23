using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-219 against a real server. A materialized view's <c>USING</c> and <c>TABLESPACE</c> were dropped, so a view meant for another tablespace was created in the
/// default one. Both are now deployed as written, converge, are changed in place, are checked for before anything runs, and are scaffolded.
/// </summary>
[TestClass]
public sealed class MaterializedViewTablespaceEndToEndTests
{
    [TestMethod]
    [PostgreSqlVersion(Minimum = 15)]
    public async Task TheTablespaceAndAccessMethodAreDeployedChangedInPlaceAndScaffolded()
    {
        const string tablespace = "g219_ts";
        var databaseName = await CreateDatabaseAsync("g219");
        var projectDirectory = CreateProjectDirectory("g219");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g219-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            await PostgresContainerFixture.CreateTablespaceDirectoryAsync("/tmp/g219_ts");
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE TABLESPACE {tablespace} LOCATION '/tmp/g219_ts';");
            var connectionString = ConnectionStringFor(databaseName);
            // A second table access method, which pgproj does not model: it is the database's, made here as a DBA would.
            await ExecuteAsync(connectionString, "CREATE ACCESS METHOD heap2 TYPE TABLE HANDLER heap_tableam_handler;");
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, $"CREATE TABLE public.t (a int);\nCREATE MATERIALIZED VIEW public.mv USING heap TABLESPACE {tablespace} AS SELECT a FROM public.t;\n");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual($"{tablespace} heap", await ScalarTextAsync(connectionString, "SELECT ts.spcname || ' ' || am.amname FROM pg_class c JOIN pg_tablespace ts ON ts.oid = c.reltablespace JOIN pg_am am ON am.oid = c.relam WHERE c.oid = 'public.mv'::regclass;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            WriteSchema(projectDirectory, $"CREATE TABLE public.t (a int);\nCREATE MATERIALIZED VIEW public.mv USING heap2 TABLESPACE {tablespace} AS SELECT a FROM public.t;\n");
            var altered = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, altered.ExitCode, "the second publish failed.\n" + altered.Output);
            Assert.AreEqual("heap2", await ScalarTextAsync(connectionString, "SELECT am.amname FROM pg_class c JOIN pg_am am ON am.oid = c.relam WHERE c.oid = 'public.mv'::regclass;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            StringAssert.Contains(File.ReadAllText(Directory.GetFiles(scaffoldDirectory, "mv.sql", SearchOption.AllDirectories).Single()), $"USING heap2 TABLESPACE {tablespace} AS");
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP TABLESPACE IF EXISTS {tablespace};");
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }

    [TestMethod]
    public async Task AMissingTablespaceIsRefusedBeforeAnythingRuns()
    {
        var databaseName = await CreateDatabaseAsync("g219m");
        var projectDirectory = CreateProjectDirectory("g219m");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, "CREATE TABLE public.t (a int);\nCREATE MATERIALIZED VIEW public.mv TABLESPACE g219_missing AS SELECT a FROM public.t;\n");

            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(1, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "tablespace \"g219_missing\", named by public.mv");
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_class WHERE relname = 't';"), "Nothing was run.");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
