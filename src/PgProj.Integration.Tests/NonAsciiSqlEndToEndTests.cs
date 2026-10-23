using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-125 against a real server: SQL with an emoji, an accented letter, and CJK text builds, deploys the values as written, re-diffs
/// <c>0/0/0</c>, and round-trips through <c>init</c>. Before, an emoji failed <c>build</c> ("Cannot marshal") and an accented enum
/// label or CHECK literal was read as U+FFFD and deployed that way.
/// </summary>
[TestClass]
public sealed class NonAsciiSqlEndToEndTests
{
    private const string Sql = """
        -- Stimmung 😀 und Café
        CREATE TYPE public.mood AS ENUM ('café', 'naïve', '日本語', 'ok😀');

        CREATE TABLE public.person (
            id integer PRIMARY KEY,
            name text NOT NULL DEFAULT 'zoë' CONSTRAINT person_name_ck CHECK (name <> 'ünknown😀'),
            feeling public.mood
        );
        COMMENT ON TABLE public.person IS 'Personen – café 😀';
        """;

    [TestMethod]
    public async Task NonAsciiSqlDeploysAsWrittenConvergesAndRoundTripsThroughInit()
    {
        var databaseName = $"g125_{Guid.NewGuid():N}";
        var scaffoldDatabaseName = $"g125s_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g125-" + Guid.NewGuid().ToString("N"));
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g125s-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));
        Directory.CreateDirectory(scaffoldDirectory);

        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {databaseName};");
        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {scaffoldDatabaseName};");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g125", "version": "1.0", "source": "./sql" }""");
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "schema.sql"), Sql);

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            StringAssert.Contains(built.Output, "No diagnostics.");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
            await AssertDeployedAsWrittenAsync(connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var scaffoldedSql = string.Concat(Directory.EnumerateFiles(scaffoldDirectory, "*.sql", SearchOption.AllDirectories).Order().Select(File.ReadAllText));
            StringAssert.Contains(scaffoldedSql, "'café'");
            StringAssert.Contains(scaffoldedSql, "'ok😀'");
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            var scaffoldConnectionString = ConnectionStringFor(scaffoldDatabaseName);
            var deployedScaffold = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", scaffoldConnectionString, "--execute");
            Assert.AreEqual(0, deployedScaffold.ExitCode, deployedScaffold.Output);
            await AssertDiffIsEmptyAsync(projectFile, scaffoldConnectionString);
            await AssertDeployedAsWrittenAsync(scaffoldConnectionString);
        }
        finally
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE);");
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP DATABASE IF EXISTS {scaffoldDatabaseName} WITH (FORCE);");
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }

    private static async Task AssertDeployedAsWrittenAsync(string connectionString)
    {
        Assert.AreEqual(
            "café|naïve|日本語|ok😀",
            await ScalarAsync(connectionString, "SELECT string_agg(enumlabel, '|' ORDER BY enumsortorder) FROM pg_enum WHERE enumtypid = 'public.mood'::regtype;"));
        Assert.AreEqual("Personen – café 😀", await ScalarAsync(connectionString, "SELECT obj_description('public.person'::regclass, 'pg_class');"));
        StringAssert.Contains(
            await ScalarAsync(connectionString, "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'person_name_ck';"),
            "'ünknown😀'");
        StringAssert.Contains(
            await ScalarAsync(connectionString, "SELECT pg_get_expr(adbin, adrelid) FROM pg_attrdef WHERE adrelid = 'public.person'::regclass;"),
            "'zoë'");
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
