using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-116 against a real server: an object a publish drops and recreates keeps its privileges. A materialized view whose query changed
/// came back with its comment but without its grant, and the next <c>diff</c> reported the grant missing. With grants modeled, the
/// project's grants are issued again; with <c>excludeGrants</c>, the privileges the database had are.
/// </summary>
[TestClass]
public sealed class RecreatedObjectPrivilegeEndToEndTests
{
    private const string Before = """
        CREATE SCHEMA s;
        CREATE TABLE s.c (id int PRIMARY KEY, n int);
        CREATE MATERIALIZED VIEW s.m AS SELECT id FROM s.c;
        CREATE FUNCTION s.f(p int) RETURNS int LANGUAGE sql AS $$ SELECT p $$;
        COMMENT ON MATERIALIZED VIEW s.m IS 'totals';
        """;

    private const string After = """
        CREATE SCHEMA s;
        CREATE TABLE s.c (id int PRIMARY KEY, n int);
        CREATE MATERIALIZED VIEW s.m AS SELECT id, n FROM s.c;
        CREATE FUNCTION s.f(p int) RETURNS bigint LANGUAGE sql AS $$ SELECT p::bigint $$;
        COMMENT ON MATERIALIZED VIEW s.m IS 'totals';
        """;

    private const string Grants = """

        GRANT SELECT ON s.m TO g116_reader;
        GRANT EXECUTE ON FUNCTION s.f(int) TO g116_reader;
        """;

    private const string Check = "SELECT has_table_privilege('g116_reader', 's.m', 'select')::text || '|' || has_function_privilege('g116_reader', 's.f(int)', 'execute')::text || '|' || coalesce(obj_description('s.m'::regclass), '');";

    [TestMethod]
    public async Task ARecreatedObjectKeepsTheProjectsGrants()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var projectFile = await WriteProjectAsync(directory, excludeGrants: false, Before + Grants);
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);

            await File.WriteAllTextAsync(Path.Combine(directory, "sql", "schema.sql"), After + Grants);
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, published.ExitCode, published.Output);

            Assert.AreEqual("true|true|totals", await ScalarAsync(connectionString, Check));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        });
    }

    [TestMethod]
    public async Task WithoutModeledGrantsTheDatabasesPrivilegesAreKept()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var projectFile = await WriteProjectAsync(directory, excludeGrants: true, Before);
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);
            await ExecuteAsync(connectionString, Grants);

            await File.WriteAllTextAsync(Path.Combine(directory, "sql", "schema.sql"), After);
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, published.ExitCode, published.Output);

            Assert.AreEqual("true|true|totals", await ScalarAsync(connectionString, Check));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        });
    }

    private static async Task WithDatabaseAsync(Func<string, string, Task> test)
    {
        var databaseName = $"g116_{Guid.NewGuid():N}";
        var directory = Path.Combine(Path.GetTempPath(), "pgproj-g116-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "sql"));
        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {databaseName};");
        await ExecuteAsync(PostgresContainerFixture.ConnectionString, "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'g116_reader') THEN CREATE ROLE g116_reader; END IF; END $$;");
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

    private static async Task<string> WriteProjectAsync(string directory, bool excludeGrants, string sql)
    {
        var projectFile = Path.Combine(directory, "pgproj.json");
        var exclude = excludeGrants ? ", \"excludeGrants\": true" : string.Empty;
        await File.WriteAllTextAsync(projectFile, "{ \"name\": \"g116\", \"version\": \"1.0\", \"source\": \"./sql\"" + exclude + " }");
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
