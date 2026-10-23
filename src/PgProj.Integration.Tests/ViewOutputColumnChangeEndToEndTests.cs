using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-104 against a real server, with the audit's repro: <c>CREATE OR REPLACE VIEW</c> failed with <c>42P16: cannot drop columns from view</c>
/// and <c>cannot change name of view column</c>. A view whose output columns change that way is now dropped and created (with the views on
/// it, keeping its grant), while one that only gains a column at the end is still replaced in place.
/// </summary>
[TestClass]
public sealed class ViewOutputColumnChangeEndToEndTests
{
    private const string Table = "CREATE SCHEMA s; CREATE TABLE s.c (id int PRIMARY KEY, name text NOT NULL, score int);";
    private const string OnTop = " CREATE VIEW s.w AS SELECT name FROM s.v; GRANT SELECT ON s.v TO g104_reader;";

    [TestMethod]
    [DataRow("SELECT c.name FROM s.c c", DisplayName = "a column removed")]
    [DataRow("SELECT c.name, c.id FROM s.c c", DisplayName = "columns reordered")]
    public async Task AReshapedViewIsDroppedAndCreated(string query)
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var projectFile = await WriteProjectAsync(directory, Table + " CREATE VIEW s.v AS SELECT c.id, c.name FROM s.c c;" + OnTop);
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);

            await File.WriteAllTextAsync(Path.Combine(directory, "sql", "schema.sql"), Table + $" CREATE VIEW s.v AS {query};" + OnTop);
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);

            Assert.AreEqual("true", await ScalarAsync(connectionString, "SELECT has_table_privilege('g104_reader', 's.v', 'select')::text;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        });
    }

    [TestMethod]
    public async Task AColumnAddedAtTheEndIsReplacedInPlace()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var projectFile = await WriteProjectAsync(directory, Table + " CREATE VIEW s.v AS SELECT c.id, c.name FROM s.c c;" + OnTop);
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);
            var oid = await ScalarAsync(connectionString, "SELECT 's.v'::regclass::oid::text;");

            await File.WriteAllTextAsync(Path.Combine(directory, "sql", "schema.sql"), Table + " CREATE VIEW s.v AS SELECT c.id, c.name, c.score FROM s.c c;" + OnTop);
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);

            Assert.AreEqual(oid, await ScalarAsync(connectionString, "SELECT 's.v'::regclass::oid::text;"), "The view was replaced, not recreated.");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        });
    }

    private static async Task WithDatabaseAsync(Func<string, string, Task> test)
    {
        var databaseName = $"g104_{Guid.NewGuid():N}";
        var directory = Path.Combine(Path.GetTempPath(), "pgproj-g104-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "sql"));
        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {databaseName};");
        await ExecuteAsync(PostgresContainerFixture.ConnectionString, "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'g104_reader') THEN CREATE ROLE g104_reader; END IF; END $$;");
        try
        {
            await test(ConnectionStringFor(databaseName), directory);
        }
        finally
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE);");
            TryDeleteDirectory(directory);
        }
    }

    private static async Task<string> WriteProjectAsync(string directory, string sql)
    {
        var projectFile = Path.Combine(directory, "pgproj.json");
        await File.WriteAllTextAsync(projectFile, "{ \"name\": \"g104\", \"version\": \"1.0\", \"source\": \"./sql\" }");
        await File.WriteAllTextAsync(Path.Combine(directory, "sql", "schema.sql"), sql);
        return projectFile;
    }

    private static string ConnectionStringFor(string databaseName)
        => new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;

    private static async Task AssertDiffIsEmptyAsync(string projectFile, string connectionString)
    {
        var diffed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
        Assert.AreEqual(0, diffed.ExitCode, diffed.Output);
        Assert.Contains("Added: 0", diffed.Output, StringComparison.Ordinal);
        Assert.Contains("Removed: 0", diffed.Output, StringComparison.Ordinal);
        Assert.Contains("Modified: 0", diffed.Output, StringComparison.Ordinal);
    }

    private static async Task<string> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync())!;
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
