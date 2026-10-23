using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-111 against a real server: a change to a composite type that a table column uses is deployed in place with <c>ALTER TYPE</c>.
/// Before, every field change was <c>DROP TYPE</c> + <c>CREATE TYPE</c>, and <c>publish --execute</c> failed with <c>2BP01</c>.
/// </summary>
[TestClass]
public sealed class CompositeTypeInPlaceEndToEndTests
{
    private const string Table = "CREATE TABLE s.t (id integer, a s.addr);";

    [TestMethod]
    public async Task FieldsOfATypeInUseAreAddedAndDroppedInPlaceAndTheRowIsKept()
    {
        await WithProjectAsync(async (write, run, scalar) =>
        {
            await write("CREATE SCHEMA s; CREATE TYPE s.addr AS (street text, zip varchar(10)); " + Table);
            await PublishAsync(run, "--execute");
            await scalar("INSERT INTO s.t VALUES (1, ROW('main', '12345'));");

            await write("CREATE SCHEMA s; CREATE TYPE s.addr AS (street text, zip varchar(10), country text); " + Table);
            var added = await PublishAsync(run, "--execute");
            StringAssert.Contains(added, "ALTER TYPE \"s\".\"addr\" ADD ATTRIBUTE \"country\" text;");
            Assert.DoesNotContain("DROP TYPE", added, StringComparison.Ordinal);
            Assert.AreEqual("(main,12345,)", await scalar("SELECT a::text FROM s.t;"));
            await AssertDiffIsEmptyAsync(run);

            await write("CREATE SCHEMA s; CREATE TYPE s.addr AS (street text, country text); " + Table);
            var refused = await run(["publish", "--execute"]);
            Assert.AreNotEqual(0, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "field \"zip\" of composite type \"s\".\"addr\" dropped; the values it holds in stored rows are lost");
            Assert.AreEqual("(main,12345,)", await scalar("SELECT a::text FROM s.t;"));

            await PublishAsync(run, "--execute", "--allow-destructive");
            Assert.AreEqual("(main,)", await scalar("SELECT a::text FROM s.t;"));
            await AssertDiffIsEmptyAsync(run);
        });
    }

    [TestMethod]
    public async Task AFieldIsRetypedInPlaceWhenNoColumnUsesTheTypeAndRefusedWithoutDamageWhenOneDoes()
    {
        await WithProjectAsync(async (write, run, scalar) =>
        {
            const string function = "CREATE FUNCTION s.street(v s.addr) RETURNS text LANGUAGE sql AS $$ SELECT (v).street $$;";
            await write("CREATE SCHEMA s; CREATE TYPE s.addr AS (street text, zip varchar(10)); " + function);
            await PublishAsync(run, "--execute");

            await write("CREATE SCHEMA s; CREATE TYPE s.addr AS (street text, zip varchar(20)); " + function);
            var retyped = await PublishAsync(run, "--execute");
            StringAssert.Contains(retyped, "ALTER TYPE \"s\".\"addr\" ALTER ATTRIBUTE \"zip\" TYPE varchar(20);");
            Assert.AreEqual("character varying(20)", await scalar("SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 's.addr'::regclass AND attname = 'zip';"));
            await AssertDiffIsEmptyAsync(run);

            await write("CREATE SCHEMA s; CREATE TYPE s.addr AS (street text, zip varchar(20)); " + function + " " + Table);
            await PublishAsync(run, "--execute");
            await write("CREATE SCHEMA s; CREATE TYPE s.addr AS (street text, zip varchar(30)); " + function + " " + Table);
            var refused = await run(["publish", "--execute"]);
            Assert.AreNotEqual(0, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "cannot alter type \"addr\" because column \"t.a\" uses it");
            Assert.AreEqual("character varying(20)", await scalar("SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 's.addr'::regclass AND attname = 'zip';"));
        });
    }

    private static async Task<string> PublishAsync(Func<string[], Task<(int ExitCode, string Output)>> run, params string[] options)
    {
        var dryRun = await run(["publish", "--dry-run"]);
        var published = await run(["publish", .. options]);
        Assert.AreEqual(0, published.ExitCode, "publish failed.\n" + published.Output);
        return dryRun.Output;
    }

    private static async Task AssertDiffIsEmptyAsync(Func<string[], Task<(int ExitCode, string Output)>> run)
    {
        var diffed = await run(["diff"]);
        Assert.AreEqual(0, diffed.ExitCode, diffed.Output);
        Assert.Contains("Added: 0", diffed.Output, StringComparison.Ordinal);
        Assert.Contains("Removed: 0", diffed.Output, StringComparison.Ordinal);
        Assert.Contains("Modified: 0", diffed.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A scratch database and project; the callback gets a writer for the project's one SQL file, a runner that adds
    /// <c>--project</c> and <c>--target-connection</c> to a command, and a scalar query (text, or null) against the database.
    /// </summary>
    private static async Task WithProjectAsync(
        Func<Func<string, Task>, Func<string[], Task<(int ExitCode, string Output)>>, Func<string, Task<string?>>, Task> test)
    {
        var databaseName = $"g111_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g111-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {databaseName};");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g111", "version": "1.0", "source": "./sql" }""");

            await test(
                sql => File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "schema.sql"), sql),
                args => RunAsync([.. args, "--project", projectFile, "--target-connection", connectionString]),
                sql => ScalarAsync(connectionString, sql));
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
        return (await command.ExecuteScalarAsync())?.ToString();
    }

    private static string ConnectionStringFor(string databaseName)
        => new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;

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
