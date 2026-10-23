using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-89: two same-named tables in different schemas, each with its own indexes, must publish against a real server
/// (it used to crash with "An item with the same key has already been added"), re-diff empty, and removing one of the
/// tables must leave the other table's index in place.
/// </summary>
[TestClass]
public sealed class SameNamedTableIndexEndToEndTests
{
    private const string BothTables = """
        CREATE SCHEMA a;
        CREATE SCHEMA b;
        CREATE TABLE a.pp (id integer PRIMARY KEY, emails jsonb);
        CREATE TABLE b.pp (id integer PRIMARY KEY, emails jsonb);
        CREATE INDEX a_pp_emails_idx ON a.pp USING GIN (emails);
        CREATE INDEX b_pp_emails_idx ON b.pp USING GIN (emails);
        """;

    private const string OnlyA = """
        CREATE SCHEMA a;
        CREATE SCHEMA b;
        CREATE TABLE a.pp (id integer PRIMARY KEY, emails jsonb);
        CREATE INDEX a_pp_emails_idx ON a.pp USING GIN (emails);
        """;

    [TestMethod]
    public async Task PublishExecute_SameNamedTablesInTwoSchemas_EachGetsItsOwnIndex_AndReDiffsEmpty()
    {
        var databaseName = $"g89_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g89-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await CreateDatabaseAsync(databaseName);
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            var sqlFile = Path.Combine(projectDirectory, "sql", "schema.sql");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g89", "version": "1.0", "source": "./sql" }""");
            await File.WriteAllTextAsync(sqlFile, BothTables);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);

            CollectionAssert.AreEquivalent(
                new[] { "a.pp.a_pp_emails_idx", "a.pp.pp_pkey", "b.pp.b_pp_emails_idx", "b.pp.pp_pkey" },
                await ReadIndexesAsync(connectionString));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // Removing b.pp must not drop a.pp's index along with it.
            await File.WriteAllTextAsync(sqlFile, OnlyA);
            var removed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, removed.ExitCode, "publish --execute (removal) failed.\n" + removed.Output);

            CollectionAssert.AreEquivalent(
                new[] { "a.pp.a_pp_emails_idx", "a.pp.pp_pkey" },
                await ReadIndexesAsync(connectionString));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            try
            {
                Directory.Delete(projectDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp directory.
            }
        }
    }

    private static async Task AssertDiffIsEmptyAsync(string projectFile, string connectionString)
    {
        var diffed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
        Assert.AreEqual(0, diffed.ExitCode, diffed.Output);
        Assert.Contains("Added: 0", diffed.Output, StringComparison.Ordinal);
        Assert.Contains("Removed: 0", diffed.Output, StringComparison.Ordinal);
        Assert.Contains("Modified: 0", diffed.Output, StringComparison.Ordinal);
    }

    private static async Task<string[]> ReadIndexesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT schemaname || '.' || tablename || '.' || indexname FROM pg_catalog.pg_indexes WHERE schemaname IN ('a', 'b');",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var indexes = new List<string>();
        while (await reader.ReadAsync())
        {
            indexes.Add(reader.GetString(0));
        }

        return indexes.ToArray();
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(params string[] args)
    {
        var context = PostgresContainerFixture.CreateCliContext();
        var root = new RootCommand("pgproj") { BuildCommand.Create(context), DiffCommand.Create(context), PublishCommand.Create(context) };

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

    private static async Task CreateDatabaseAsync(string databaseName)
    {
        await using var connection = new NpgsqlConnection(PostgresContainerFixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {databaseName};", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(string databaseName)
    {
        await using var connection = new NpgsqlConnection(PostgresContainerFixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE);", connection);
        await command.ExecuteNonQueryAsync();
    }
}
