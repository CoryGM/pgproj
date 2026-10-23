using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-114 against a real server: a project written without a single schema qualifier describes the objects in <c>public</c>. It
/// publishes, converges, publishes nothing the second time (no drop and recreate), and an <c>init</c> scaffold of it does the same.
/// Before G-114 every unqualified object was keyed without a schema and never matched what the server read back.
/// </summary>
[TestClass]
public sealed class UnqualifiedProjectEndToEndTests
{
    private const string Sql = """
        CREATE TABLE parent (id int PRIMARY KEY, name text NOT NULL);
        CREATE TABLE child (id serial PRIMARY KEY, parent_id int REFERENCES parent (id), qty int CHECK (qty > 0));
        ALTER TABLE child ADD COLUMN note text;
        CREATE INDEX child_parent_idx ON child (parent_id);
        CREATE SEQUENCE counter;
        CREATE TYPE mood AS ENUM ('ok', 'sad');
        CREATE VIEW parent_names AS SELECT id, name FROM parent;
        CREATE FUNCTION touch() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
        CREATE TRIGGER child_touch BEFORE UPDATE ON child FOR EACH ROW EXECUTE FUNCTION touch();
        COMMENT ON TABLE child IS 'children';
        GRANT SELECT ON parent TO PUBLIC;
        """;

    [TestMethod]
    public async Task AnUnqualifiedProjectConverges_AndRepublishesNothing()
    {
        var databaseName = $"g114_{Guid.NewGuid():N}";
        var scaffoldDatabaseName = $"g114s_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g114-" + Guid.NewGuid().ToString("N"));
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g114s-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));
        Directory.CreateDirectory(scaffoldDirectory);

        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {databaseName};");
        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {scaffoldDatabaseName};");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g114", "version": "1.0", "source": "./sql" }""");
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "schema.sql"), Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("public", await ScalarAsync(connectionString, "SELECT relnamespace::regnamespace::text FROM pg_class WHERE relname = 'child';"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var tableOid = await ScalarAsync(connectionString, "SELECT 'public.child'::regclass::oid::text;");
            var scriptPath = Path.Combine(projectDirectory, "second.sql");
            var republished = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--output", scriptPath);
            Assert.AreEqual(0, republished.ExitCode, republished.Output);
            var secondScript = File.Exists(scriptPath) ? await File.ReadAllTextAsync(scriptPath) : string.Empty;
            foreach (var verb in new[] { "CREATE", "DROP", "ALTER", "GRANT", "REVOKE", "COMMENT" })
            {
                Assert.IsFalse(secondScript.Contains(verb + " ", StringComparison.Ordinal), secondScript);
            }

            Assert.AreEqual(tableOid, await ScalarAsync(connectionString, "SELECT 'public.child'::regclass::oid::text;"), "The table was rebuilt.");

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            var scaffoldConnectionString = ConnectionStringFor(scaffoldDatabaseName);
            var deployedScaffold = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", scaffoldConnectionString, "--execute");
            Assert.AreEqual(0, deployedScaffold.ExitCode, deployedScaffold.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, scaffoldConnectionString);
            await AssertDiffIsEmptyAsync(projectFile, scaffoldConnectionString);
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
