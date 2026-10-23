using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-95 against a real server, with the audit's repro: a routine with a SQL-standard body (<c>BEGIN ATOMIC ... END</c>, <c>RETURN
/// expression</c>) was a TODO placeholder that <c>publish --execute</c> refused. It is now created as written, compares equal once deployed
/// though PostgreSQL stores it rewritten, is changed when its body changes, round-trips through <c>init</c>, and, since PostgreSQL records
/// what such a body uses, is dropped and recreated around a column change or a routine rebuild it would otherwise block (G-103, G-105).
/// </summary>
[TestClass]
public sealed class SqlStandardRoutineBodyEndToEndTests
{
    // a_top comes first in the source and calls z_leaf, which is declared after it (the audit's repro).
    private const string Schema = """
        CREATE SCHEMA s;
        CREATE TABLE s.t (id int PRIMARY KEY, a int);
        CREATE FUNCTION s.a_top() RETURNS int LANGUAGE sql BEGIN ATOMIC SELECT s.z_leaf() + 1; END;
        CREATE FUNCTION s.z_leaf() RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;
        CREATE FUNCTION s.total(p int) RETURNS bigint LANGUAGE sql STABLE BEGIN ATOMIC SELECT CASE WHEN p > 0 THEN sum(a) ELSE 0 END FROM s.t; END;
        CREATE FUNCTION s.twice(x int) RETURNS int LANGUAGE sql IMMUTABLE RETURN x * 2;
        CREATE PROCEDURE s.clear() LANGUAGE sql BEGIN ATOMIC DELETE FROM s.t; END;
        """;

    [TestMethod]
    public async Task SqlStandardBodiesArePublishedAndThenCompareEqual()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var projectFile = await WriteProjectAsync(directory, Schema);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);

            Assert.AreEqual("2|4|0", await ScalarAsync(connectionString, "SELECT s.a_top() || '|' || s.twice(2) || '|' || s.total(0);"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        });
    }

    [TestMethod]
    public async Task AChangedBodyIsDeployed()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var projectFile = await WriteProjectAsync(directory, Schema);
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);

            await File.WriteAllTextAsync(Path.Combine(directory, "sql", "schema.sql"), Schema.Replace("x * 2", "x * 3", StringComparison.Ordinal).Replace("ELSE 0", "ELSE -1", StringComparison.Ordinal));
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);

            Assert.AreEqual("6|-1", await ScalarAsync(connectionString, "SELECT s.twice(2) || '|' || s.total(0);"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        });
    }

    [TestMethod]
    public async Task AnAtomicRoutineInTheWayIsDroppedAndRecreated()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var projectFile = await WriteProjectAsync(directory, Schema);
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);

            // s.total uses column s.t.a, which is retyped (G-103); s.a_top uses s.z_leaf, whose result type changes (G-105).
            var changed = Schema
                .Replace("a int)", "a bigint)", StringComparison.Ordinal)
                .Replace("s.z_leaf() + 1", "s.z_leaf()::int + 1", StringComparison.Ordinal)
                .Replace("s.z_leaf() RETURNS int", "s.z_leaf() RETURNS bigint", StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(directory, "sql", "schema.sql"), changed);
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, published.ExitCode, published.Output);

            Assert.AreEqual("bigint|2", await ScalarAsync(connectionString, "SELECT format_type(atttypid, -1) || '|' || s.a_top() FROM pg_attribute WHERE attrelid = 's.t'::regclass AND attname = 'a';"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        });
    }

    [TestMethod]
    public async Task AnInitScaffoldRoundTrips()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var projectFile = await WriteProjectAsync(directory, Schema);
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);

            var scaffoldFile = Path.Combine(directory, "scaffold", "pgproj.json");
            var initialized = await RunAsync("init", "--project", scaffoldFile, "--source-connection", connectionString);
            Assert.AreEqual(0, initialized.ExitCode, initialized.Output);

            await AssertDiffIsEmptyAsync(scaffoldFile, connectionString);
        });
    }

    private static async Task WithDatabaseAsync(Func<string, string, Task> test)
    {
        var databaseName = $"g95_{Guid.NewGuid():N}";
        var directory = Path.Combine(Path.GetTempPath(), "pgproj-g95-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "sql"));
        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {databaseName};");
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
        await File.WriteAllTextAsync(projectFile, "{ \"name\": \"g95\", \"version\": \"1.0\", \"source\": \"./sql\" }");
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
