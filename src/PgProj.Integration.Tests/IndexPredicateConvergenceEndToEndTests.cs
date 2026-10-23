using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-100 against a real server: PostgreSQL stores a partial index's predicate with each literal typed by the column it is compared
/// with (<c>status &lt;&gt; 'cancelled'</c> on an enum is <c>status &lt;&gt; 'cancelled'::s.st</c>; a date, <c>timestamptz</c>, or
/// <c>uuid</c> literal gets its type too, a <c>timestamptz</c> one its offset), so such an index re-diffed after every deploy and was
/// dropped and recreated on every publish. The project's predicate is now rendered by the server too.
/// </summary>
[TestClass]
public sealed class IndexPredicateConvergenceEndToEndTests
{
    private const string Sql = """
        CREATE SCHEMA s;
        CREATE TYPE s.st AS ENUM ('new', 'cancelled');
        CREATE TABLE s.o (id int, cid bigint, status s.st, d date, ts timestamptz, u uuid);
        CREATE INDEX o_open_idx ON s.o (cid, status) WHERE status <> 'cancelled';
        CREATE INDEX o_kinds_idx ON s.o (id) WHERE status IN ('new', 'cancelled');
        CREATE INDEX o_recent_idx ON s.o (d) WHERE d > '2026-01-01';
        CREATE INDEX o_ts_idx ON s.o (ts) WHERE ts > '2026-01-01';
        CREATE INDEX o_u_idx ON s.o (u) WHERE u <> '00000000-0000-0000-0000-000000000000';
        """;

    [TestMethod]
    public async Task PartialIndexesWithTypedLiteralsConverge_AndARealPredicateChangeIsStillDeployed()
    {
        var databaseName = $"g100_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g100-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {databaseName};");
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            var sqlFile = Path.Combine(projectDirectory, "sql", "schema.sql");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g100", "version": "1.0", "source": "./sql" }""");
            await File.WriteAllTextAsync(sqlFile, Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            await AssertDiffAsync(projectFile, connectionString, "Modified: 0");

            var indexOid = await ScalarAsync(connectionString, "SELECT 's.o_open_idx'::regclass::oid::text;");
            var scriptPath = Path.Combine(projectDirectory, "second.sql");
            var republished = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--output", scriptPath);
            Assert.AreEqual(0, republished.ExitCode, republished.Output);
            var secondScript = File.Exists(scriptPath) ? await File.ReadAllTextAsync(scriptPath) : string.Empty;
            Assert.IsFalse(secondScript.Contains("DROP INDEX", StringComparison.Ordinal), secondScript);
            Assert.AreEqual(indexOid, await ScalarAsync(connectionString, "SELECT 's.o_open_idx'::regclass::oid::text;"), "The index was rebuilt.");

            // A real change to a predicate is still seen, deployed, and converges.
            await File.WriteAllTextAsync(sqlFile, Sql.Replace("WHERE status <> 'cancelled'", "WHERE status <> 'new'", StringComparison.Ordinal));
            await AssertDiffAsync(projectFile, connectionString, "Modified: 2"); // the index and its table
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            StringAssert.Contains(await ScalarAsync(connectionString, "SELECT pg_get_indexdef('s.o_open_idx'::regclass);"), "'new'::s.st");
            await AssertDiffAsync(projectFile, connectionString, "Modified: 0");
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

    private static async Task AssertDiffAsync(string projectFile, string connectionString, string expectedModified)
    {
        var diffed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
        Assert.AreEqual(0, diffed.ExitCode, diffed.Output);
        Assert.Contains("Added: 0", diffed.Output, StringComparison.Ordinal);
        Assert.Contains("Removed: 0", diffed.Output, StringComparison.Ordinal);
        Assert.Contains(expectedModified, diffed.Output, StringComparison.Ordinal);
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
}
