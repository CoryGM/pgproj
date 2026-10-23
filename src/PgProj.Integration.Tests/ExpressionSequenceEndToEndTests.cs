using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-174 against a real server. A view, a materialized view, a <c>CHECK</c> constraint, and a policy that name a sequence are created
/// after it. Each is named so that it sorts before the sequence, so only the dependency puts the script in a runnable order.
/// </summary>
[TestClass]
public sealed class ExpressionSequenceEndToEndTests
{
    private const string Sql = """
        CREATE VIEW s.a_view AS SELECT nextval('s.z_view_seq') AS n;
        CREATE MATERIALIZED VIEW s.a_matview AS SELECT 1 AS x WHERE false AND currval('s.z_view_seq'::regclass) > 0 WITH NO DATA;
        CREATE TABLE s.a_table (id bigint, CONSTRAINT a_ck CHECK (id < currval('s.z_check_seq'::regclass)));
        CREATE TABLE s.b_table (id bigint);
        ALTER TABLE s.b_table ENABLE ROW LEVEL SECURITY;
        CREATE POLICY a_pol ON s.b_table USING (id < currval('s.z_policy_seq'::regclass));
        CREATE SEQUENCE s.z_view_seq;
        CREATE SEQUENCE s.z_check_seq;
        CREATE SEQUENCE s.z_policy_seq;
        """;

    [TestMethod]
    public async Task ASequenceNamedInAnExpressionIsCreatedFirst_AndTheProjectConverges()
    {
        var databaseName = $"g174_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g174-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {databaseName};");
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g174", "version": "1.0", "source": "./sql", "schemas": ["s"] }""");
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "schema.sql"), Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);

            Assert.AreEqual("1", await ScalarAsync(connectionString, "SELECT n::text FROM s.a_view;"));

            var diffed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            Assert.AreEqual(0, diffed.ExitCode, diffed.Output);
            Assert.Contains("Added: 0", diffed.Output, StringComparison.Ordinal);
            Assert.Contains("Removed: 0", diffed.Output, StringComparison.Ordinal);
            Assert.Contains("Modified: 0", diffed.Output, StringComparison.Ordinal);
        }
        finally
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE);");
            TryDeleteDirectory(projectDirectory);
        }
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
