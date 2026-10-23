using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-115 against a real server: a range type converges however its options are written. The live loader read only the subtype, through
/// <c>format_type</c>, so a subtype spelled differently (<c>float8</c>) or any other option (<c>SUBTYPE_DIFF</c>, <c>COLLATION</c>,
/// <c>SUBTYPE_OPCLASS</c>) re-diffed after every publish and the type was dropped and recreated. And a quoted option was deployed without
/// its quotes (<c>COLLATION = C</c>, which PostgreSQL folds to <c>c</c> and rejects).
/// </summary>
[TestClass]
public sealed class RangeTypeOptionsEndToEndTests
{
    private const string Sql = """
        CREATE SCHEMA s;
        CREATE FUNCTION s.fdiff(a float8, b float8) RETURNS float8 LANGUAGE sql IMMUTABLE AS $$ SELECT a - b $$;
        CREATE TYPE public.r1 AS RANGE (subtype = float8, subtype_diff = float8mi);
        CREATE TYPE public.r2 AS RANGE (subtype = int4);
        CREATE TYPE public.r3 AS RANGE (subtype = text, collation = "C");
        CREATE TYPE public.r4 AS RANGE (subtype = numeric(10,2));
        CREATE TYPE public.r5 AS RANGE (subtype = text, subtype_opclass = text_pattern_ops);
        CREATE TYPE public.r6 AS RANGE (subtype = timestamp with time zone, subtype_diff = pg_catalog.tstzrange_subdiff);
        CREATE TYPE public.r7 AS RANGE (subtype = double precision, subtype_diff = s.fdiff);
        CREATE TYPE public.r8 AS RANGE (subtype = int4[]);
        CREATE TABLE public.t (id int PRIMARY KEY, span public.r1);
        """;

    [TestMethod]
    public async Task RangeTypesConvergeHoweverTheirOptionsAreWritten()
    {
        var databaseName = $"g115_{Guid.NewGuid():N}";
        var scaffoldDatabaseName = $"g115s_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g115-" + Guid.NewGuid().ToString("N"));
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g115s-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));
        Directory.CreateDirectory(scaffoldDirectory);

        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {databaseName};");
        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {scaffoldDatabaseName};");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            var sqlFile = Path.Combine(projectDirectory, "sql", "schema.sql");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g115", "version": "1.0", "source": "./sql" }""");
            await File.WriteAllTextAsync(sqlFile, Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("C", await ScalarAsync(connectionString, "SELECT collname FROM pg_range JOIN pg_collation c ON c.oid = rngcollation WHERE rngtypid = 'public.r3'::regtype;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var typeOid = await ScalarAsync(connectionString, "SELECT 'public.r1'::regtype::oid::text;");
            var scriptPath = Path.Combine(projectDirectory, "second.sql");
            var republished = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--output", scriptPath);
            Assert.AreEqual(0, republished.ExitCode, republished.Output);
            var secondScript = File.Exists(scriptPath) ? await File.ReadAllTextAsync(scriptPath) : string.Empty;
            Assert.IsFalse(secondScript.Contains("Type", StringComparison.Ordinal), secondScript);
            Assert.AreEqual(typeOid, await ScalarAsync(connectionString, "SELECT 'public.r1'::regtype::oid::text;"), "The type was recreated.");

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);
            var scaffoldConnectionString = ConnectionStringFor(scaffoldDatabaseName);
            var deployedScaffold = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", scaffoldConnectionString, "--execute");
            Assert.AreEqual(0, deployedScaffold.ExitCode, deployedScaffold.Output);
            await AssertDiffIsEmptyAsync(projectFile, scaffoldConnectionString);

            // A real change is still one.
            await File.WriteAllTextAsync(sqlFile, Sql.Replace("subtype = float8, subtype_diff = float8mi", "subtype = float8", StringComparison.Ordinal));
            var changed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            Assert.Contains("Modified: 1", changed.Output, StringComparison.Ordinal);
            Assert.Contains("~ Type public.r1", changed.Output, StringComparison.Ordinal);
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
