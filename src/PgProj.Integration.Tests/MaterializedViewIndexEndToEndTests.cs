using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-171 against a real server. An index on a materialized view was read as an index on a missing table, so it was never deployed (and
/// <c>REFRESH MATERIALIZED VIEW CONCURRENTLY</c>, which needs a unique index, could not be used). It is now created after the view,
/// compared, changed, and removed like a table's; a view that is dropped and created again to be changed gets its indexes back in the
/// same publish; and <c>init</c> writes the indexes into the view's file.
/// </summary>
[TestClass]
public sealed class MaterializedViewIndexEndToEndTests
{
    private const string Sql = """
        CREATE TABLE s.t (id int, grp int, amount numeric);
        CREATE MATERIALIZED VIEW s.mv AS SELECT grp, sum(amount) AS total, count(*) AS n FROM s.t GROUP BY grp;
        CREATE UNIQUE INDEX a_mv_grp ON s.mv (grp);
        CREATE INDEX b_mv_total ON s.mv (total DESC) WHERE n > 1;
        COMMENT ON INDEX s.a_mv_grp IS 'one row per group';
        """;

    [TestMethod]
    public async Task IndexesOnAMaterializedViewAreDeployedChangedRecreatedAndScaffolded()
    {
        var databaseName = $"g171_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g171-" + Guid.NewGuid().ToString("N"));
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g171s-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));
        Directory.CreateDirectory(scaffoldDirectory);

        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {databaseName};");
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            var sqlFile = Path.Combine(projectDirectory, "sql", "schema.sql");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g171", "version": "1.0", "source": "./sql", "schemas": ["s"] }""");
            await File.WriteAllTextAsync(sqlFile, Sql);

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            StringAssert.Contains(built.Output, "No diagnostics.");

            // Deployed after the view, and converges.
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("a_mv_grp,b_mv_total", await ScalarAsync(connectionString, IndexNames));
            Assert.AreEqual("one row per group", await ScalarAsync(connectionString, "SELECT obj_description('s.a_mv_grp'::regclass, 'pg_class');"));
            await AssertDiffAsync(projectFile, connectionString, "Modified: 0");

            // The unique index is what lets the view be refreshed without locking readers.
            await ExecuteAsync(connectionString, "REFRESH MATERIALIZED VIEW CONCURRENTLY s.mv;");

            // A changed index, a new index, and a removed index are each seen and deployed, and converge.
            await File.WriteAllTextAsync(sqlFile, Sql
                .Replace("(total DESC) WHERE n > 1", "(total, n)", StringComparison.Ordinal)
                + "CREATE INDEX c_mv_n ON s.mv (n);\n");
            await AssertDiffAsync(projectFile, connectionString, "Modified: 1", expectedAdded: 1);
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            Assert.AreEqual("a_mv_grp,b_mv_total,c_mv_n", await ScalarAsync(connectionString, IndexNames));
            StringAssert.Contains(await ScalarAsync(connectionString, "SELECT indexdef FROM pg_indexes WHERE indexname = 'b_mv_total';"), "(total, n)");
            await AssertDiffAsync(projectFile, connectionString, "Modified: 0");

            await File.WriteAllTextAsync(sqlFile, Sql.Replace("CREATE INDEX b_mv_total ON s.mv (total DESC) WHERE n > 1;\n", string.Empty, StringComparison.Ordinal));
            await AssertDiffAsync(projectFile, connectionString, "Modified: 0", expectedRemoved: 2);
            var removed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, removed.ExitCode, removed.Output);
            Assert.AreEqual("a_mv_grp", await ScalarAsync(connectionString, IndexNames));
            await AssertDiffAsync(projectFile, connectionString, "Modified: 0");

            // Changing the view drops it and creates it again; the indexes it had come back in the same publish, so it can still be refreshed.
            await File.WriteAllTextAsync(sqlFile, Sql.Replace("count(*) AS n", "count(*) AS n, max(amount) AS biggest", StringComparison.Ordinal));
            var recreated = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, recreated.ExitCode, recreated.Output);
            Assert.AreEqual("a_mv_grp,b_mv_total", await ScalarAsync(connectionString, IndexNames));
            await AssertDiffAsync(projectFile, connectionString, "Modified: 0");
            await ExecuteAsync(connectionString, "REFRESH MATERIALIZED VIEW CONCURRENTLY s.mv;");

            // init writes the indexes into the view's own file; the scaffold builds clean and has nothing to deploy.
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var viewFile = Directory.EnumerateFiles(scaffoldDirectory, "mv.sql", SearchOption.AllDirectories).Single();
            var viewText = await File.ReadAllTextAsync(viewFile);
            StringAssert.Contains(viewText, "CREATE MATERIALIZED VIEW");
            StringAssert.Contains(viewText, "a_mv_grp");
            StringAssert.Contains(viewText, "b_mv_total");
            var scaffoldBuilt = await RunAsync("build", "--project", scaffoldProject);
            Assert.AreEqual(0, scaffoldBuilt.ExitCode, scaffoldBuilt.Output);
            StringAssert.Contains(scaffoldBuilt.Output, "No diagnostics.");
            await AssertDiffAsync(scaffoldProject, connectionString, "Modified: 0");
        }
        finally
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE);");
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }

    private const string IndexNames = "SELECT string_agg(indexname, ',' ORDER BY indexname) FROM pg_indexes WHERE schemaname = 's' AND tablename = 'mv';";

    private static async Task AssertDiffAsync(string projectFile, string connectionString, string expectedModified, int expectedAdded = 0, int expectedRemoved = 0)
    {
        var diffed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
        Assert.AreEqual(0, diffed.ExitCode, diffed.Output);
        Assert.Contains($"Added: {expectedAdded}", diffed.Output, StringComparison.Ordinal);
        Assert.Contains($"Removed: {expectedRemoved}", diffed.Output, StringComparison.Ordinal);
        Assert.Contains(expectedModified, diffed.Output, StringComparison.Ordinal);
    }

    private static async Task<string?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(params string[] args)
    {
        var context = PostgresContainerFixture.CreateCliContext();
        var root = new RootCommand("pgproj")
        {
            InitCommand.Create(context),
            BuildCommand.Create(context),
            DiffCommand.Create(context),
            PublishCommand.Create(context),
        };

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(output);
        try
        {
            var exitCode = await root.Parse(args).InvokeAsync();
            return (exitCode, output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp directory.
        }
    }
}
