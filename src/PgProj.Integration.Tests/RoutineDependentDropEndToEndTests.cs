using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-105 against a real server, starting from the audit's repro: changing a function's return type needs <c>DROP FUNCTION</c>, which
/// failed with <c>2BP01: cannot drop function ... because other objects depend on it</c> while a column default used it. Everything the
/// database records as using the routine is now taken off before the drop and put back after the create, and something pgproj doesn't
/// model is refused before anything runs.
/// </summary>
[TestClass]
public sealed class RoutineDependentDropEndToEndTests
{
    private const string Users = """

        CREATE TABLE s.c (id int PRIMARY KEY, code int NOT NULL DEFAULT s.next_code(1), n int, CONSTRAINT c_code_check CHECK (s.next_code(n) >= 0));
        CREATE INDEX c_code_idx ON s.c ((s.next_code(n)));
        CREATE INDEX c_part_idx ON s.c (id) WHERE s.next_code(n) > 0;
        CREATE VIEW s.v AS SELECT id, s.next_code(n) AS nc FROM s.c;
        ALTER TABLE s.c ENABLE ROW LEVEL SECURITY;
        CREATE POLICY p ON s.c USING (s.next_code(n) >= 0);
        CREATE FUNCTION s.tf() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
        CREATE TRIGGER tr BEFORE UPDATE ON s.c FOR EACH ROW WHEN (s.next_code(NEW.n) > 0) EXECUTE FUNCTION s.tf();
        CREATE DOMAIN s.pos AS int CHECK (s.next_code(VALUE) >= 0);
        GRANT EXECUTE ON FUNCTION s.next_code(int) TO g105_reader;
        """;

    private const string Int = "CREATE SCHEMA s; CREATE FUNCTION s.next_code(x int) RETURNS int LANGUAGE sql IMMUTABLE AS $$ SELECT x $$;";
    private const string BigInt = "CREATE SCHEMA s; CREATE FUNCTION s.next_code(x int) RETURNS bigint LANGUAGE sql IMMUTABLE AS $$ SELECT x::bigint $$;";

    [TestMethod]
    public async Task TheAuditsRepro_ADefaultUsingTheFunction()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var projectFile = await WriteProjectAsync(directory, Int + " CREATE TABLE s.c (id int PRIMARY KEY, code int NOT NULL DEFAULT s.next_code(1), name text);");
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);

            await File.WriteAllTextAsync(Path.Combine(directory, "sql", "schema.sql"), BigInt + " CREATE TABLE s.c (id int PRIMARY KEY, code int NOT NULL DEFAULT s.next_code(1), name text);");
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);

            await ExecuteAsync(connectionString, "INSERT INTO s.c (id) VALUES (1);");
            Assert.AreEqual("bigint|1", await ScalarAsync(connectionString, "SELECT pg_get_function_result('s.next_code(int)'::regprocedure) || '|' || (SELECT code FROM s.c)::text;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        });
    }

    [TestMethod]
    public async Task EveryKindOfUserIsTakenOffAndPutBack()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var projectFile = await WriteProjectAsync(directory, Int + Users);
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);
            await ExecuteAsync(connectionString, "ALTER FUNCTION s.next_code(int) OWNER TO g105_owner; INSERT INTO s.c (id, n) VALUES (1, 5);");

            await File.WriteAllTextAsync(Path.Combine(directory, "sql", "schema.sql"), BigInt + Users);
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);

            Assert.AreEqual(
                "bigint|g105_owner|true|1",
                await ScalarAsync(connectionString, "SELECT pg_get_function_result(p.oid) || '|' || pg_get_userbyid(p.proowner) || '|' || has_function_privilege('g105_reader', p.oid, 'execute')::text || '|' || (SELECT count(*) FROM s.c)::text FROM pg_proc p WHERE p.oid = 's.next_code(int)'::regprocedure;"));
            Assert.AreEqual(
                "c_code_check:true,c_pkey:true,pos_check:true",
                await ScalarAsync(connectionString, "SELECT string_agg(conname || ':' || convalidated::text, ',' ORDER BY conname) FROM pg_constraint WHERE (conrelid = 's.c'::regclass OR contypid = 's.pos'::regtype) AND contype <> 'n';"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        });
    }

    [TestMethod]
    public async Task SomethingPgprojDoesNotModelIsRefusedBeforeAnythingRuns()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var projectFile = await WriteProjectAsync(directory, Int);
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);
            // An operator backed by the function: PostgreSQL won't drop the function under it, and pgproj doesn't model operators.
            await ExecuteAsync(connectionString, "CREATE OPERATOR s.#! (RIGHTARG = int, FUNCTION = s.next_code);");

            await File.WriteAllTextAsync(Path.Combine(directory, "sql", "schema.sql"), BigInt);
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(1, published.ExitCode, published.Output);
            StringAssert.Contains(published.Output, "operator");
            Assert.AreEqual("integer", await ScalarAsync(connectionString, "SELECT pg_get_function_result('s.next_code(int)'::regprocedure);"), "Nothing ran.");
        });
    }

    private static async Task WithDatabaseAsync(Func<string, string, Task> test)
    {
        var databaseName = $"g105_{Guid.NewGuid():N}";
        var directory = Path.Combine(Path.GetTempPath(), "pgproj-g105-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "sql"));
        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {databaseName};");
        await ExecuteAsync(PostgresContainerFixture.ConnectionString, "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'g105_reader') THEN CREATE ROLE g105_reader; END IF; IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'g105_owner') THEN CREATE ROLE g105_owner; END IF; END $$;");
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

    private static async Task<string> WriteProjectAsync(string directory, string sql)
    {
        var projectFile = Path.Combine(directory, "pgproj.json");
        await File.WriteAllTextAsync(projectFile, "{ \"name\": \"g105\", \"version\": \"1.0\", \"source\": \"./sql\" }");
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
