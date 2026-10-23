using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-112 against a real server: a publish keeps the source order of what it emits (a UNIQUE constraint's columns, which PostgreSQL
/// also uses as its index's column order; a table's constraints and indexes; grant privileges and columns), while an order-only change to
/// the source is not a change and publishes nothing.
/// </summary>
[TestClass]
public sealed class SourceOrderEndToEndTests
{
    // Not the public schema: an index on a public table re-diffs for an unrelated reason (G-113).
    private const string Sql = """
        CREATE SCHEMA app;
        CREATE TABLE app.parent (id int NOT NULL);
        CREATE TABLE app.t (z int, b int, a int, parent_id int);
        ALTER TABLE app.t ADD CONSTRAINT t_zz_uq UNIQUE (z, b);
        ALTER TABLE app.t ADD CONSTRAINT t_parent_fk FOREIGN KEY (parent_id) REFERENCES app.parent (id);
        ALTER TABLE app.parent ADD CONSTRAINT parent_pk PRIMARY KEY (id);
        CREATE INDEX t_z_idx ON app.t (z);
        CREATE INDEX t_a_idx ON app.t (a);
        GRANT UPDATE, SELECT, INSERT ON app.t TO PUBLIC;
        GRANT UPDATE (z, a) ON app.t TO PUBLIC;
        """;

    [TestMethod]
    public async Task APublishKeepsSourceOrder_AndAnOrderOnlyChangePublishesNothing()
    {
        var databaseName = $"g112_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g112-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {databaseName};");
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            var sqlFile = Path.Combine(projectDirectory, "sql", "schema.sql");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g112", "version": "1.0", "source": "./sql" }""");
            await File.WriteAllTextAsync(sqlFile, Sql);

            var scriptPath = Path.Combine(projectDirectory, "deploy.sql");
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--output", scriptPath);
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);

            var script = await File.ReadAllTextAsync(scriptPath);
            StringAssert.Contains(script, "UNIQUE (\"z\", \"b\")");
            StringAssert.Contains(script, "GRANT UPDATE, SELECT, INSERT ON TABLE \"app\".\"t\" TO PUBLIC;");
            StringAssert.Contains(script, "GRANT UPDATE (\"z\", \"a\") ON TABLE \"app\".\"t\" TO PUBLIC;");
            Assert.IsTrue(script.IndexOf("\"t_z_idx\"", StringComparison.Ordinal) < script.IndexOf("\"t_a_idx\"", StringComparison.Ordinal), script);
            Assert.IsTrue(script.IndexOf("\"parent_pk\"", StringComparison.Ordinal) < script.IndexOf("\"t_parent_fk\"", StringComparison.Ordinal), "A foreign key still waits for its key.\n" + script);

            // The unique index PostgreSQL built for the constraint has the written column order.
            StringAssert.Contains(await ScalarAsync(connectionString, "SELECT pg_get_indexdef('app.t_zz_uq'::regclass);"), "(z, b)");
            await AssertDiffAsync(projectFile, connectionString);

            // Reordering the source, and nothing else, is not a change.
            var reordered = EndToEndSupport.Edit(Sql, "UNIQUE (z, b)", "UNIQUE (b, z)");
            reordered = EndToEndSupport.Edit(reordered, "GRANT UPDATE, SELECT, INSERT", "GRANT INSERT, SELECT, UPDATE");
            reordered = EndToEndSupport.Edit(reordered, "UPDATE (z, a)", "UPDATE (a, z)");
            reordered = EndToEndSupport.Edit(reordered, "CREATE INDEX t_z_idx ON app.t (z);\nCREATE INDEX t_a_idx ON app.t (a);", "CREATE INDEX t_a_idx ON app.t (a);\nCREATE INDEX t_z_idx ON app.t (z);");
            await File.WriteAllTextAsync(sqlFile, reordered);
            await AssertDiffAsync(projectFile, connectionString);
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

    private static async Task AssertDiffAsync(string projectFile, string connectionString)
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
