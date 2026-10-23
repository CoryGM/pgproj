using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-113 against a real server, in the <c>public</c> schema: <c>pg_get_indexdef</c> prints an index on a <c>public</c> table without the
/// schema (<c>ON t</c>), so every such index was read back under a different key and dropped and recreated on every publish. Earlier index
/// tests all used another schema, which is why this went unseen.
/// </summary>
[TestClass]
public sealed class PublicSchemaIndexEndToEndTests
{
    private const string Sql = """
        CREATE TABLE public.orders (id int PRIMARY KEY, customer_id int, status text, note text);
        CREATE INDEX orders_customer_idx ON public.orders (customer_id);
        CREATE UNIQUE INDEX orders_open_uidx ON public.orders (customer_id) WHERE status <> 'closed';
        CREATE INDEX orders_note_lower_idx ON public.orders (lower(note));
        COMMENT ON INDEX public.orders_customer_idx IS 'lookup by customer';
        """;

    [TestMethod]
    public async Task IndexesOnAPublicTableConverge_AndAnInitScaffoldOfThemDoesToo()
    {
        var databaseName = $"g113_{Guid.NewGuid():N}";
        var scaffoldDatabaseName = $"g113s_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g113-" + Guid.NewGuid().ToString("N"));
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g113s-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));
        Directory.CreateDirectory(scaffoldDirectory);

        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {databaseName};");
        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {scaffoldDatabaseName};");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g113", "version": "1.0", "source": "./sql" }""");
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "schema.sql"), Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var indexOid = await ScalarAsync(connectionString, "SELECT 'public.orders_customer_idx'::regclass::oid::text;");
            var scriptPath = Path.Combine(projectDirectory, "second.sql");
            var republished = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--output", scriptPath);
            Assert.AreEqual(0, republished.ExitCode, republished.Output);
            var secondScript = File.Exists(scriptPath) ? await File.ReadAllTextAsync(scriptPath) : string.Empty;
            Assert.IsFalse(secondScript.Contains("INDEX", StringComparison.Ordinal), secondScript);
            Assert.AreEqual(indexOid, await ScalarAsync(connectionString, "SELECT 'public.orders_customer_idx'::regclass::oid::text;"), "The index was rebuilt.");

            // init writes each index as pg_get_indexdef prints it (ON orders); that project builds clean, deploys, and converges.
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var built = await RunAsync("build", "--project", scaffoldProject);
            StringAssert.Contains(built.Output, "No diagnostics.");
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            var scaffoldConnectionString = ConnectionStringFor(scaffoldDatabaseName);
            var deployedScaffold = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", scaffoldConnectionString, "--execute");
            Assert.AreEqual(0, deployedScaffold.ExitCode, deployedScaffold.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, scaffoldConnectionString);
        }
        finally
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE);");
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP DATABASE IF EXISTS {scaffoldDatabaseName} WITH (FORCE);");
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
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
