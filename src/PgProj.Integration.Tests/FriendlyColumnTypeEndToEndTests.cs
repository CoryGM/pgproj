using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-88, as G-207 changed it: a SQL-standard keyword type spelling (<c>integer</c>, <c>varchar(20)</c>, <c>timestamptz</c>) must
/// deploy as the project wrote it, never pg_query's internal <c>pg_catalog.int4</c>-style form, against a real server - and the
/// generated script must still be exactly what gets executed and re-diffs empty afterward. G-88 deployed a fixed friendly spelling
/// (<c>character varying(40)</c> for <c>varchar(40)</c>); the user reversed that on 2026-10-11, so the written spelling is asserted.
/// </summary>
[TestClass]
public sealed class FriendlyColumnTypeEndToEndTests
{
    private const string Sql = """
        CREATE TABLE public.widgets (
            id integer PRIMARY KEY,
            price numeric(10,2),
            name varchar(40),
            created_at timestamptz DEFAULT now()
        );
        """;

    [TestMethod]
    public async Task PublishExecute_UsesTheTypesAsWritten_AndReDiffsEmpty()
    {
        var databaseName = $"g88_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g88-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await CreateDatabaseAsync(databaseName);
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g88", "version": "1.0", "source": "./sql" }""");
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "schema.sql"), Sql);

            var scriptPath = Path.Combine(projectDirectory, "deploy.sql");
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--output", scriptPath);
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);

            // --output writes the exact script that --execute ran, regardless of what's printed to the console.
            var script = await File.ReadAllTextAsync(scriptPath);
            Assert.Contains("\"id\" integer NOT NULL", script, StringComparison.Ordinal);
            Assert.Contains("\"price\" numeric(10,2)", script, StringComparison.Ordinal);
            Assert.Contains("\"name\" varchar(40)", script, StringComparison.Ordinal);
            Assert.Contains("\"created_at\" timestamptz", script, StringComparison.Ordinal);
            Assert.IsFalse(script.Contains("pg_catalog.", StringComparison.Ordinal), script);

            var diffed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            Assert.AreEqual(0, diffed.ExitCode, diffed.Output);
            Assert.Contains("Added: 0", diffed.Output, StringComparison.Ordinal);
            Assert.Contains("Removed: 0", diffed.Output, StringComparison.Ordinal);
            Assert.Contains("Modified: 0", diffed.Output, StringComparison.Ordinal);
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
