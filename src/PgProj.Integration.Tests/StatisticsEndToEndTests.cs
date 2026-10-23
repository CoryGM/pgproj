using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-150 against a real server, on every version: extended statistics (kinds written and not, an expression, a column pair) deploy after their table whatever order the file
/// has, read back from <c>pg_statistic_ext</c>, converge <c>0/0/0</c> although PostgreSQL prints the expression and the kinds its own way, round-trip through <c>init</c> into
/// their table's file, are changed by a drop and a create, and are removed along with a column they use.
/// </summary>
[TestClass]
public sealed class StatisticsEndToEndTests
{
    private static string Schema(string kinds = "(dependencies, ndistinct)", bool withMcv = true, string columns = "a int, b int, c text")
        => $"""
            CREATE STATISTICS app.t_ab {kinds} ON a, b FROM app.t;
            CREATE STATISTICS app.t_expr ON (lower(c)), a FROM app.t;
            {(withMcv ? "CREATE STATISTICS app.t_mcv (mcv) ON a, b FROM app.t;" : string.Empty)}
            CREATE SCHEMA app;
            CREATE TABLE app.t ({columns});
            """;

    private const string Query = """
        SELECT string_agg(s.stxname || ':' || array_to_string(s.stxkind, '') || ':' || coalesce(array_length(s.stxkeys::int2[], 1), 0)::text, '|' ORDER BY s.stxname)
        FROM pg_statistic_ext s JOIN pg_namespace n ON n.oid = s.stxnamespace WHERE n.nspname = 'app';
        """;

    [TestMethod]
    public async Task StatisticsDeployConvergeRoundTripThroughInitAndAreChangedOrRemovedSafely()
    {
        var databaseName = await CreateDatabaseAsync("g150");
        var scaffoldDatabaseName = await CreateDatabaseAsync("g150s");
        var projectDirectory = CreateProjectDirectory("g150");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Schema());

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            StringAssert.Contains(built.Output, "No diagnostics.");

            // The statistics come before the schema and the table in the file, and still deploy after them.
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("3", await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_statistic_ext s JOIN pg_namespace n ON n.oid = s.stxnamespace WHERE n.nspname = 'app';"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.IsFalse(again.Output.Contains("STATISTICS", StringComparison.Ordinal), again.Output);

            // Written another way: the kinds reordered, the columns swapped, the expression in other parentheses.
            WriteSchema(projectDirectory, Schema(kinds: "(ndistinct, dependencies)").Replace("ON a, b FROM app.t;\nCREATE STATISTICS app.t_expr", "ON b, a FROM app.t;\nCREATE STATISTICS app.t_expr", StringComparison.Ordinal).Replace("(lower(c))", "(LOWER((c)))", StringComparison.Ordinal));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
            WriteSchema(projectDirectory, Schema());

            // Round trip through init: each object is written beside its table, and the scaffold deploys the same into a second database.
            var scaffoldDirectory = Path.Combine(projectDirectory, "scaffold");
            var scaffolded = await RunAsync("init", "--source-connection", connectionString, "--output", scaffoldDirectory, "--project", Path.Combine(scaffoldDirectory, "pgproj.json"));
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var tableFiles = Directory.GetFiles(scaffoldDirectory, "*.sql", SearchOption.AllDirectories).Where(static f => File.ReadAllText(f).Contains("CREATE STATISTICS", StringComparison.Ordinal)).ToArray();
            Assert.AreEqual(1, tableFiles.Length, string.Join("; ", tableFiles));
            StringAssert.Contains(File.ReadAllText(tableFiles[0]), "CREATE TABLE");
            await AssertDiffIsEmptyAsync(Path.Combine(scaffoldDirectory, "pgproj.json"), connectionString);
            var scaffoldConnectionString = ConnectionStringFor(scaffoldDatabaseName);
            var deployedScaffold = await RunAsync("publish", "--project", Path.Combine(scaffoldDirectory, "pgproj.json"), "--target-connection", scaffoldConnectionString, "--execute");
            Assert.AreEqual(0, deployedScaffold.ExitCode, deployedScaffold.Output);
            Assert.AreEqual(await ScalarTextAsync(connectionString, Query), await ScalarTextAsync(scaffoldConnectionString, Query));
            await AssertDiffIsEmptyAsync(projectFile, scaffoldConnectionString);

            // A change to the kinds is a drop and a create, and the object is not lost.
            var oidBefore = await ScalarTextAsync(connectionString, "SELECT oid::text FROM pg_statistic_ext WHERE stxname = 't_ab';");
            WriteSchema(projectDirectory, Schema(kinds: "(mcv)"));
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            Assert.AreNotEqual(oidBefore, await ScalarTextAsync(connectionString, "SELECT oid::text FROM pg_statistic_ext WHERE stxname = 't_ab';"));
            Assert.AreEqual("m", await ScalarTextAsync(connectionString, "SELECT array_to_string(stxkind, '') FROM pg_statistic_ext WHERE stxname = 't_ab';"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // One removed is not a destructive change.
            WriteSchema(projectDirectory, Schema(kinds: "(mcv)", withMcv: false));
            var removed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, removed.ExitCode, removed.Output);
            Assert.AreEqual("2", await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_statistic_ext s JOIN pg_namespace n ON n.oid = s.stxnamespace WHERE n.nspname = 'app';"));

            // Dropping a column that statistics use drops them with it; the project says so by dropping the statistics too.
            WriteSchema(projectDirectory, """
                CREATE SCHEMA app;
                CREATE TABLE app.t (a int, c text);
                CREATE STATISTICS app.t_ac ON a, c FROM app.t;
                """);
            var dropped = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, dropped.ExitCode, dropped.Output);
            Assert.AreEqual("t_ac", await ScalarTextAsync(connectionString, "SELECT string_agg(stxname, '|') FROM pg_statistic_ext s JOIN pg_namespace n ON n.oid = s.stxnamespace WHERE n.nspname = 'app';"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await DropDatabaseAsync(scaffoldDatabaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task AProjectThatListsSchemasOnlyManagesTheStatisticsInThem()
    {
        var databaseName = await CreateDatabaseAsync("g150sc");
        var projectDirectory = CreateProjectDirectory("g150sc", ", \"schemas\": [\"app\"]");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, "CREATE TABLE public.other (a int, b int); CREATE STATISTICS public.other_ab ON a, b FROM public.other;");
            WriteSchema(projectDirectory, "CREATE SCHEMA app; CREATE TABLE app.t (a int, b int); CREATE STATISTICS app.t_ab ON a, b FROM app.t;");

            var published = await RunAsync("publish", "--project", ProjectFile(projectDirectory), "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("2", await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_statistic_ext;"));
            await AssertDiffIsEmptyAsync(ProjectFile(projectDirectory), connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
