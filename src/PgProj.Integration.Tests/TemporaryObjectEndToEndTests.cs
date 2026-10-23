using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-117 against a real server: a temporary table, view, or sequence is reported by <c>build</c> and <c>publish</c>, which still
/// succeed, and is not created. Before, the same project built with "No diagnostics." and publish created each one as a
/// permanent object in the <c>public</c> schema.
/// </summary>
[TestClass]
public sealed class TemporaryObjectEndToEndTests
{
    private const string Sql = """
        CREATE TABLE public.t (a integer);

        CREATE TEMP TABLE g117_tmp (a integer);
        CREATE TEMPORARY VIEW g117_tv AS SELECT 1 AS one;
        CREATE TEMP SEQUENCE g117_ts;
        """;

    [TestMethod]
    public async Task TemporaryObjectsAreReportedAndNotCreated()
    {
        var databaseName = $"g117_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g117-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {databaseName};");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g117", "version": "1.0", "source": "./sql" }""");
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "schema.sql"), Sql);

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            StringAssert.Contains(built.Output, "schema.sql:3:1-3:39: [Warning] CREATE TEMPORARY TABLE is not modeled (a temporary object lasts only for the session that creates it); the statement is ignored: CREATE TEMP TABLE g117_tmp (a integer)");
            StringAssert.Contains(built.Output, "schema.sql:4:1-4:49: [Warning] CREATE TEMPORARY VIEW is not modeled (a temporary object lasts only for the session that creates it); the statement is ignored: CREATE TEMPORARY VIEW g117_tv AS SELECT 1 AS one");
            StringAssert.Contains(built.Output, "schema.sql:5:1-5:29: [Warning] CREATE TEMPORARY SEQUENCE is not modeled (a temporary object lasts only for the session that creates it); the statement is ignored: CREATE TEMP SEQUENCE g117_ts");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);

            Assert.AreEqual(1L, await ScalarAsync(connectionString, "SELECT count(*) FROM pg_class WHERE relname = 't' AND relnamespace = 'public'::regnamespace;"));
            Assert.AreEqual(0L, await ScalarAsync(connectionString, "SELECT count(*) FROM pg_class WHERE relname LIKE 'g117%';"));

            var diffed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            Assert.AreEqual(0, diffed.ExitCode, diffed.Output);
            Assert.Contains("Added: 0", diffed.Output, StringComparison.Ordinal);
            Assert.Contains("Removed: 0", diffed.Output, StringComparison.Ordinal);
            Assert.Contains("Modified: 0", diffed.Output, StringComparison.Ordinal);
        }
        finally
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE);");
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
