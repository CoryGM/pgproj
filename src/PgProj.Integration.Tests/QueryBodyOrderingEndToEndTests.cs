using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-90 against a real server, with the 2026-09-23 audit's shapes. PostgreSQL resolves a view's query and a <c>LANGUAGE sql</c> body when
/// it creates them, so each must come after what it reads. A view over a view (G-91), a schema-qualified call in a view's select list
/// (G-92), and a <c>LANGUAGE sql</c> function calling another (G-94) each either failed <c>build</c> (with <c>schemas</c> set, a false
/// "missing" error) or failed <c>publish</c> (<c>42P01</c>/<c>42883</c>). Every object is named so that name order is the wrong order.
/// </summary>
[TestClass]
public sealed class QueryBodyOrderingEndToEndTests
{
    private const string Sql = """
        CREATE SCHEMA shop;
        CREATE TABLE shop.customer (id bigint PRIMARY KEY, name text NOT NULL);
        CREATE TABLE shop.orders (id bigint PRIMARY KEY, customer_id bigint REFERENCES shop.customer (id), amount numeric NOT NULL);
        CREATE VIEW shop.a_big_customers AS SELECT t.id, t.total FROM shop.z_customer_totals t WHERE t.total > 100;
        CREATE VIEW shop.a_named_totals AS SELECT c.name, t.total FROM shop.customer c JOIN shop.z_customer_totals t ON t.id = c.id;
        CREATE VIEW shop.z_customer_totals AS SELECT c.id, shop.z_order_total(c.id) AS total FROM shop.customer c;
        CREATE FUNCTION shop.a_grand_total() RETURNS numeric LANGUAGE sql AS $$ SELECT coalesce(sum(shop.z_order_total(c.id)), 0) FROM shop.customer c $$;
        CREATE FUNCTION shop.a_top_count() RETURNS bigint LANGUAGE sql AS $$ SELECT count(*) FROM shop.a_big_customers $$;
        CREATE FUNCTION shop.z_order_total(p_customer bigint) RETURNS numeric LANGUAGE sql AS $$ SELECT coalesce(sum(o.amount), 0) FROM shop.orders o WHERE o.customer_id = p_customer $$;
        """;

    [TestMethod]
    public async Task ViewsAndSqlFunctionsAreCreatedAfterWhatTheyRead()
    {
        var databaseName = $"g90_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g90-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {databaseName};");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g90", "version": "1.0", "source": "./sql", "schemas": ["shop"] }""");
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "schema.sql"), Sql);

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            StringAssert.Contains(built.Output, "No diagnostics.");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            await ExecuteAsync(connectionString, "INSERT INTO shop.customer VALUES (1, 'a'), (2, 'b'); INSERT INTO shop.orders VALUES (1, 1, 150), (2, 2, 20);");
            Assert.AreEqual("170", await ScalarAsync(connectionString, "SELECT shop.a_grand_total()::text;"));
            Assert.AreEqual("1", await ScalarAsync(connectionString, "SELECT shop.a_top_count()::text;"));
        }
        finally
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE);");
            TryDeleteDirectory(projectDirectory);
        }
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
