using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-99 against a real server: a composite type's fields read back in PostgreSQL's spelling (<c>character varying(10)</c>,
/// <c>integer</c>, <c>timestamp with time zone</c>), so any field written another way (<c>varchar(10)</c>, <c>int</c>,
/// <c>timestamptz</c>) made the type re-diff after every deploy and be dropped and recreated on every publish, which fails
/// (2BP01) once a column uses the type. Fields are now compared on their parsed, canonical type.
/// </summary>
[TestClass]
public sealed class CompositeTypeConvergenceEndToEndTests
{
    private const string Sql = """
        CREATE SCHEMA s;
        CREATE TYPE s.st AS ENUM ('a', 'b');
        CREATE TYPE s.addr AS (
            street text,
            zip varchar(10),
            code char(3),
            n int,
            ts timestamptz,
            arr int[],
            vc varchar,
            amt numeric(10,2),
            e s.st
        );
        CREATE TABLE s.customer (id int PRIMARY KEY, address s.addr);
        """;

    [TestMethod]
    public async Task ACompositeTypeInUseConverges_AndARealFieldChangeIsStillReported()
    {
        var databaseName = $"g99_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g99-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {databaseName};");
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            var sqlFile = Path.Combine(projectDirectory, "sql", "schema.sql");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g99", "version": "1.0", "source": "./sql" }""");
            await File.WriteAllTextAsync(sqlFile, Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            await AssertDiffAsync(projectFile, connectionString, "Modified: 0");

            // The type is used by a column: a second publish that tried to drop and recreate it would fail (2BP01).
            var scriptPath = Path.Combine(projectDirectory, "second.sql");
            var republished = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--output", scriptPath);
            Assert.AreEqual(0, republished.ExitCode, "second publish --execute failed.\n" + republished.Output);
            var secondScript = File.Exists(scriptPath) ? await File.ReadAllTextAsync(scriptPath) : string.Empty;
            Assert.IsFalse(secondScript.Contains("DROP TYPE", StringComparison.Ordinal), secondScript);

            // A real change to a field is still seen (deploying it to a type in use is G-111).
            await File.WriteAllTextAsync(sqlFile, Sql.Replace("zip varchar(10)", "zip varchar(20)", StringComparison.Ordinal));
            await AssertDiffAsync(projectFile, connectionString, "Modified: 1");
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
