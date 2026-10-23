using System.CommandLine;
using System.Text.Json;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-107 against a real server: <c>pgproj diff</c> lists each change (text, the default) and ends with the counts it always printed,
/// and <c>--format json</c> writes only a JSON document on standard output (the project header and diagnostics go to standard error),
/// so CI can parse it directly.
/// </summary>
[TestClass]
public sealed class DiffOutputEndToEndTests
{
    [TestMethod]
    public async Task DiffListsTheChangeSetAsText_AndAsJsonOnStandardOutput()
    {
        var databaseName = $"g107_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g107-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {databaseName};");
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            var sqlFile = Path.Combine(projectDirectory, "sql", "schema.sql");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g107", "version": "1.0", "source": "./sql" }""");
            await File.WriteAllTextAsync(sqlFile, "CREATE SCHEMA s; CREATE TABLE s.t (id int); CREATE INDEX t_id_idx ON s.t (id);");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.StandardOutput + published.StandardError);

            await File.WriteAllTextAsync(sqlFile, "CREATE SCHEMA s; CREATE TABLE s.t (id int); COMMENT ON TABLE s.t IS 'orders'; CREATE TABLE s.u (id int);");

            var text = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            Assert.AreEqual(0, text.ExitCode, text.StandardError);
            StringAssert.Contains(text.StandardOutput, "  + Table s.u");
            StringAssert.Contains(text.StandardOutput, "  - Index s.t_id_idx");
            StringAssert.Contains(text.StandardOutput, "      Comment: (none) -> 'orders'");
            StringAssert.EndsWith(text.StandardOutput.TrimEnd(), "Added: 2" + Environment.NewLine + "Removed: 1" + Environment.NewLine + "Modified: 1");

            var json = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString, "--format", "json");
            Assert.AreEqual(0, json.ExitCode, json.StandardError);
            StringAssert.Contains(json.StandardError, "Loaded project: g107");
            using var document = JsonDocument.Parse(json.StandardOutput);
            var root = document.RootElement;
            CollectionAssert.AreEquivalent(
                new[] { "s.u", "s.u.id" },
                root.GetProperty("added").EnumerateArray().Select(static change => change.GetProperty("id").GetString()).ToArray());
            Assert.AreEqual("s.t_id_idx", root.GetProperty("removed")[0].GetProperty("id").GetString());
            Assert.AreEqual("s.t", root.GetProperty("modified")[0].GetProperty("id").GetString());
            Assert.AreEqual(1, root.GetProperty("summary").GetProperty("removed").GetInt32());
        }
        finally
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE);");
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

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunAsync(params string[] args)
    {
        var context = PostgresContainerFixture.CreateCliContext();
        var root = new RootCommand("pgproj") { DiffCommand.Create(context), PublishCommand.Create(context) };

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var output = new StringWriter();
        var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var exitCode = await root.Parse(args).InvokeAsync();
            return (exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
