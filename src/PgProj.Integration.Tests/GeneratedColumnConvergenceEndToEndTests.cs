using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-97 against a real server: PostgreSQL stores a generated column's expression with the implicit casts it inserted
/// (<c>qty * price</c> over <c>integer</c> and <c>numeric</c> becomes <c>((qty)::numeric * price)</c>), so the column re-diffed after
/// every deploy and every publish dropped and re-added it, rewriting the table. The project's expression is now rendered by the server
/// too.
/// </summary>
[TestClass]
public sealed class GeneratedColumnConvergenceEndToEndTests
{
    private const string Sql = """
        CREATE TABLE public.orders (
            qty int NOT NULL,
            price numeric(12,2) NOT NULL,
            name varchar(20),
            total numeric(14,2) GENERATED ALWAYS AS (qty * price) STORED,
            ratio double precision GENERATED ALWAYS AS (qty / 3.0) STORED,
            twice bigint GENERATED ALWAYS AS (qty * 2) STORED,
            label text GENERATED ALWAYS AS (name || '!') STORED,
            flag boolean GENERATED ALWAYS AS (qty > 0 AND price BETWEEN 1 AND 9) STORED
        );
        """;

    [TestMethod]
    public async Task GeneratedColumnsWithImplicitCastsConverge_AndARealChangeIsStillDeployed()
    {
        var databaseName = $"g97_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g97-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {databaseName};");
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            var sqlFile = Path.Combine(projectDirectory, "sql", "schema.sql");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g97", "version": "1.0", "source": "./sql" }""");
            await File.WriteAllTextAsync(sqlFile, Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            await ExecuteAsync(connectionString, "INSERT INTO public.orders (qty, price, name) VALUES (3, 2.50, 'a');");
            await AssertDiffAsync(projectFile, connectionString, "Modified: 0");

            // A second publish must not drop and re-add the generated columns.
            var scriptPath = Path.Combine(projectDirectory, "second.sql");
            var republished = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--output", scriptPath);
            Assert.AreEqual(0, republished.ExitCode, republished.Output);
            var secondScript = File.Exists(scriptPath) ? await File.ReadAllTextAsync(scriptPath) : string.Empty;
            Assert.IsFalse(secondScript.Contains("DROP COLUMN", StringComparison.Ordinal), secondScript);

            // A real change to an expression is still seen, deployed, computed, and converges.
            await File.WriteAllTextAsync(sqlFile, Sql.Replace("AS (qty * price) STORED", "AS (qty * price * 2) STORED", StringComparison.Ordinal));
            await AssertDiffAsync(projectFile, connectionString, "Modified: 2"); // the column and its table
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            Assert.AreEqual("15.00", await ScalarAsync(connectionString, "SELECT total::text FROM public.orders;"));
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
