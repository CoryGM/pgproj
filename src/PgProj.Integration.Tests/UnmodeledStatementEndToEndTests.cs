using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-106 against a real server: a statement pgproj does not model is reported by <c>build</c> and <c>publish</c>, which still
/// succeed, and is not run. Before, the same project built with "No diagnostics." and its text search template, rule, and seed row were
/// silently missing after a publish.
/// </summary>
[TestClass]
public sealed class UnmodeledStatementEndToEndTests
{
    private const string Sql = """
        CREATE TABLE public.t (a integer);

        CREATE TEXT SEARCH TEMPLATE public.g106_ts
            (INIT = a, LEXIZE = b);

        CREATE FOREIGN DATA WRAPPER g106_fdw;

        CREATE SERVER s FOREIGN DATA WRAPPER g106_fdw OPTIONS (password 'g142secret');

        INSERT INTO public.t VALUES (1);
        """;

    [TestMethod]
    public async Task UnmodeledStatementsAreReportedAndNotRun()
    {
        var databaseName = $"g106_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g106-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {databaseName};");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g106", "version": "1.0", "source": "./sql" }""");
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "schema.sql"), Sql);

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            StringAssert.Contains(built.Output, "schema.sql:3:1-4:27: [Warning] CREATE TEXT SEARCH TEMPLATE is not modeled; the statement is ignored: CREATE TEXT SEARCH TEMPLATE public.g106_ts (INIT = a, LEXIZE = b)");
            StringAssert.Contains(built.Output, "schema.sql:6:1-6:37: [Warning] CREATE FOREIGN DATA WRAPPER is not modeled; the statement is ignored: CREATE FOREIGN DATA WRAPPER g106_fdw");
            StringAssert.Contains(built.Output, "schema.sql:10:1-10:32: [Warning] INSERT is not modeled; the statement is ignored (to run it on every publish, put it in the predeploy or postdeploy script): INSERT INTO public.t VALUES (1)");
            StringAssert.Contains(built.Output, "schema.sql:8:1-8:78: [Warning] CREATE SERVER is not modeled; the statement is ignored: CREATE SERVER s FOREIGN DATA WRAPPER g106_fdw OPTIONS (password '*****')");
            Assert.DoesNotContain("g142secret", built.Output, StringComparison.Ordinal);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            StringAssert.Contains(published.Output, "CREATE TEXT SEARCH TEMPLATE is not modeled");
            Assert.DoesNotContain("g142secret", published.Output, StringComparison.Ordinal);

            Assert.AreEqual(0L, await ScalarAsync(connectionString, "SELECT count(*) FROM public.t;"));
            Assert.AreEqual(0L, await ScalarAsync(connectionString, "SELECT count(*) FROM pg_ts_template WHERE tmplname = 'g106_ts';"));
            Assert.AreEqual(0L, await ScalarAsync(connectionString, "SELECT count(*) FROM pg_foreign_data_wrapper WHERE fdwname = 'g106_fdw';"));

            var diffed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            Assert.AreEqual(0, diffed.ExitCode, diffed.Output);
            Assert.Contains("Added: 0", diffed.Output, StringComparison.Ordinal);
            Assert.Contains("Removed: 0", diffed.Output, StringComparison.Ordinal);
            Assert.Contains("Modified: 0", diffed.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("g142secret", diffed.Output, StringComparison.Ordinal);
        }
        finally
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE);");
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task ASyntaxErrorDoesNotPrintAPasswordTheParserStoppedAt()
    {
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g141-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));
        try
        {
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g141", "version": "1.0", "source": "./sql" }""");
            await File.WriteAllTextAsync(
                Path.Combine(projectDirectory, "sql", "schema.sql"),
                "CREATE TABLE public.t (a integer);\n\nCREATE ROLE app LOGIN PASSWORD g141secret;\n");

            var built = await RunAsync("build", "--project", projectFile);

            Assert.AreNotEqual(0, built.ExitCode, built.Output);
            StringAssert.Contains(built.Output, "schema.sql:3:");
            StringAssert.Contains(built.Output, "syntax error at or near \"*****\"");
            Assert.DoesNotContain("g141secret", built.Output, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(projectDirectory);
        }
    }

    private static string ConnectionStringFor(string databaseName)
        => new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
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
