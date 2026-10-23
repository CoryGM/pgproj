using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-103 against a real server, with the audit's repro. A column's type change failed with <c>0A000: cannot alter type of a column used by
/// a view or rule</c>, and a dropped column with <c>2BP01</c>, because the view was still there. The views that use the column (and the views
/// built on those) are now dropped before the change and recreated after it, keeping their grants, comments, and owner; the table's data
/// is kept. A view another project owns is refused before anything runs.
/// </summary>
[TestClass]
public sealed class ViewDependentColumnChangeEndToEndTests
{
    private const string Views = """

        CREATE VIEW s.v AS SELECT c.id, c.score FROM s.c c;
        CREATE VIEW s.v2 AS SELECT id FROM s.v WHERE score > 1;
        CREATE MATERIALIZED VIEW s.mv AS SELECT id, score FROM s.c;
        CREATE VIEW s.names AS SELECT id, name FROM s.c;
        COMMENT ON VIEW s.v2 IS 'on v';
        GRANT SELECT ON s.v TO g103_reader;
        """;

    [TestMethod]
    public async Task AColumnTypeChangeUnderViewsDeploysAndKeepsWhatTheViewsHad()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var projectFile = await WriteProjectAsync(directory, null, "CREATE SCHEMA s; CREATE TABLE s.c (id int PRIMARY KEY, name text NOT NULL, score int);" + Views);
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);
            await ExecuteAsync(connectionString, "ALTER VIEW s.v2 OWNER TO g103_reader; INSERT INTO s.c VALUES (1, 'a', 5), (2, 'b', 1);");
            var namesOid = await ScalarAsync(connectionString, "SELECT 's.names'::regclass::oid::text;");

            await File.WriteAllTextAsync(Path.Combine(directory, "sql", "schema.sql"), "CREATE SCHEMA s; CREATE TABLE s.c (id int PRIMARY KEY, name text NOT NULL, score bigint);" + Views);
            var scriptPath = Path.Combine(directory, "change.sql");
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive", "--output", scriptPath);
            Assert.AreEqual(0, published.ExitCode, published.Output);
            StringAssert.Contains(await File.ReadAllTextAsync(scriptPath), "-- s.v is dropped here and recreated below: it uses column s.c.score");

            Assert.AreEqual("bigint", await ScalarAsync(connectionString, "SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 's.c'::regclass AND attname = 'score';"));
            Assert.AreEqual("2|1|2", await ScalarAsync(connectionString, "SELECT (SELECT count(*) FROM s.c)::text || '|' || (SELECT count(*) FROM s.v2)::text || '|' || (SELECT count(*) FROM s.mv)::text;"));
            Assert.AreEqual("true|on v|g103_reader", await ScalarAsync(connectionString, "SELECT has_table_privilege('g103_reader', 's.v', 'select')::text || '|' || obj_description('s.v2'::regclass) || '|' || pg_get_userbyid(relowner) FROM pg_class WHERE oid = 's.v2'::regclass;"));
            Assert.AreEqual(namesOid, await ScalarAsync(connectionString, "SELECT 's.names'::regclass::oid::text;"), "A view that doesn't use the column is left alone.");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        });
    }

    [TestMethod]
    public async Task AColumnDroppedFromUnderARewrittenViewDeploys()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var projectFile = await WriteProjectAsync(directory, null, "CREATE SCHEMA s; CREATE TABLE s.c (id int PRIMARY KEY, name text NOT NULL, score int); CREATE INDEX c_score_idx ON s.c (score); CREATE VIEW s.v AS SELECT c.id, c.score FROM s.c c;");
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);

            await File.WriteAllTextAsync(Path.Combine(directory, "sql", "schema.sql"), "CREATE SCHEMA s; CREATE TABLE s.c (id int PRIMARY KEY, name text NOT NULL); CREATE VIEW s.v AS SELECT c.id, c.name FROM s.c c;");
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        });
    }

    [TestMethod]
    public async Task APolicyAndATriggerUsingTheColumnAreRecreatedAroundIt()
    {
        // Part 2: `cannot alter type of a column used in a policy definition` / `... in a trigger definition`.
        const string Rest = """

            ALTER TABLE s.t ENABLE ROW LEVEL SECURITY;
            CREATE POLICY p ON s.t USING (a > 0);
            CREATE FUNCTION s.tf() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
            CREATE TRIGGER tr BEFORE UPDATE OF a ON s.t FOR EACH ROW WHEN (NEW.a > 0) EXECUTE FUNCTION s.tf();
            """;
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var projectFile = await WriteProjectAsync(directory, null, "CREATE SCHEMA s; CREATE TABLE s.t (id int PRIMARY KEY, a int);" + Rest);
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);
            await ExecuteAsync(connectionString, "INSERT INTO s.t VALUES (1, 2);");

            await File.WriteAllTextAsync(Path.Combine(directory, "sql", "schema.sql"), "CREATE SCHEMA s; CREATE TABLE s.t (id int PRIMARY KEY, a bigint);" + Rest);
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, published.ExitCode, published.Output);

            Assert.AreEqual("bigint|1", await ScalarAsync(connectionString, "SELECT format_type(atttypid, null) || '|' || (SELECT count(*) FROM s.t)::text FROM pg_attribute WHERE attrelid = 's.t'::regclass AND attname = 'a';"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        });
    }

    [TestMethod]
    public async Task AGeneratedColumnInTheWayIsRefusedBeforeAnythingRuns()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var projectFile = await WriteProjectAsync(directory, null, "CREATE SCHEMA s; CREATE TABLE s.t (id int PRIMARY KEY, a int, g int GENERATED ALWAYS AS (a * 2) STORED);");
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);

            await File.WriteAllTextAsync(Path.Combine(directory, "sql", "schema.sql"), "CREATE SCHEMA s; CREATE TABLE s.t (id int PRIMARY KEY, a bigint, g int GENERATED ALWAYS AS (a * 2) STORED);");
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(1, published.ExitCode, published.Output);
            StringAssert.Contains(published.Output, "s.t.g");
        });
    }

    [TestMethod]
    public async Task AViewAnotherProjectOwnsIsRefusedBeforeAnythingRuns()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var projectFile = await WriteProjectAsync(directory, "\"schemas\": [\"s\"]", "CREATE SCHEMA s; CREATE TABLE s.c (id int PRIMARY KEY, score int);");
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);
            await ExecuteAsync(connectionString, "CREATE SCHEMA other; CREATE VIEW other.report AS SELECT id, score FROM s.c;");

            await File.WriteAllTextAsync(Path.Combine(directory, "sql", "schema.sql"), "CREATE SCHEMA s; CREATE TABLE s.c (id int PRIMARY KEY, score bigint);");
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(1, published.ExitCode, published.Output);
            StringAssert.Contains(published.Output, "other.report");
            Assert.AreEqual("integer", await ScalarAsync(connectionString, "SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 's.c'::regclass AND attname = 'score';"), "Nothing ran.");
        });
    }

    private static async Task WithDatabaseAsync(Func<string, string, Task> test)
    {
        var databaseName = $"g103_{Guid.NewGuid():N}";
        var directory = Path.Combine(Path.GetTempPath(), "pgproj-g103-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "sql"));
        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {databaseName};");
        await ExecuteAsync(PostgresContainerFixture.ConnectionString, "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'g103_reader') THEN CREATE ROLE g103_reader; END IF; END $$;");
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

    private static async Task<string> WriteProjectAsync(string directory, string? extraProperty, string sql)
    {
        var projectFile = Path.Combine(directory, "pgproj.json");
        var extra = extraProperty is null ? string.Empty : ", " + extraProperty;
        await File.WriteAllTextAsync(projectFile, "{ \"name\": \"g103\", \"version\": \"1.0\", \"source\": \"./sql\"" + extra + " }");
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
