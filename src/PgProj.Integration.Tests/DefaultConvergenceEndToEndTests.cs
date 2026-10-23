using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-102 against a real server. A default written with a cast (<c>0::numeric</c>, <c>'{}'::text[]</c>, <c>'new'::s.st</c>) was
/// "unparsable" to the text parser (a warning), a cast to a schema-qualified type was read as a missing column (an error, refusing
/// <c>build</c>, once <c>schemas</c> is set), and PostgreSQL's rendering of several defaults re-diffed forever (<c>SET DEFAULT
/// 0::numeric</c> on every publish). <c>pgproj init</c> writes defaults in exactly this form, so no scaffolded project was clean.
/// </summary>
[TestClass]
public sealed class DefaultConvergenceEndToEndTests
{
    private const string Sql = """
        CREATE SCHEMA s;
        CREATE TYPE s.st AS ENUM ('new', 'old');
        CREATE SEQUENCE s.seq;
        CREATE TABLE s.t (
            id int,
            a numeric DEFAULT 0::numeric,
            c numeric(10,2) DEFAULT 1.5::numeric,
            d text[] NOT NULL DEFAULT '{}'::text[],
            e text[] DEFAULT '{}',
            f s.st NOT NULL DEFAULT 'new'::s.st,
            g s.st DEFAULT 'new',
            h int DEFAULT 0::int,
            i bigint DEFAULT 5::bigint,
            j jsonb DEFAULT '{}'::jsonb,
            k int[] DEFAULT ARRAY[]::int[],
            l varchar(5) DEFAULT 'x'::varchar,
            o boolean DEFAULT 'true'::boolean,
            q int DEFAULT -1,
            r timestamptz DEFAULT '2026-01-01'::timestamptz,
            u bigint DEFAULT nextval('s.seq'),
            v timestamptz DEFAULT now()
        );
        """;

    [TestMethod]
    public async Task CastDefaultsBuildClean_Converge_ARealChangeIsDeployed_AndAnInitScaffoldBuildsClean()
    {
        var databaseName = $"g102_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g102-" + Guid.NewGuid().ToString("N"));
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g102s-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));
        Directory.CreateDirectory(scaffoldDirectory);

        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {databaseName};");
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            var sqlFile = Path.Combine(projectDirectory, "sql", "schema.sql");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g102", "version": "1.0", "source": "./sql", "schemas": ["s"] }""");
            await File.WriteAllTextAsync(sqlFile, Sql);

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            StringAssert.Contains(built.Output, "No diagnostics.");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            await AssertDiffAsync(projectFile, connectionString, "Modified: 0");

            var scriptPath = Path.Combine(projectDirectory, "second.sql");
            var republished = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--output", scriptPath);
            Assert.AreEqual(0, republished.ExitCode, republished.Output);
            var secondScript = File.Exists(scriptPath) ? await File.ReadAllTextAsync(scriptPath) : string.Empty;
            Assert.IsFalse(secondScript.Contains("SET DEFAULT", StringComparison.Ordinal), secondScript);

            // A real change to a default is still seen, deployed, and converges.
            await File.WriteAllTextAsync(sqlFile, Sql.Replace("a numeric DEFAULT 0::numeric", "a numeric DEFAULT 1::numeric", StringComparison.Ordinal));
            await AssertDiffAsync(projectFile, connectionString, "Modified: 2"); // the column and its table
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            Assert.AreEqual("(1)::numeric", await ScalarAsync(connectionString, "SELECT pg_get_expr(adbin, adrelid) FROM pg_attrdef WHERE adrelid = 's.t'::regclass AND adnum = (SELECT attnum FROM pg_attribute WHERE attrelid = 's.t'::regclass AND attname = 'a');"));
            await AssertDiffAsync(projectFile, connectionString, "Modified: 0");

            // init writes every default in PostgreSQL's cast form; the scaffolded project builds with no diagnostics and diffs empty.
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var scaffoldBuilt = await RunAsync("build", "--project", scaffoldProject);
            Assert.AreEqual(0, scaffoldBuilt.ExitCode, scaffoldBuilt.Output);
            StringAssert.Contains(scaffoldBuilt.Output, "No diagnostics.");
            await AssertDiffAsync(scaffoldProject, connectionString, "Modified: 0");
        }
        finally
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE);");
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }

    private static async Task AssertDiffAsync(string projectFile, string connectionString, string expectedModified)
    {
        var diffed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
        Assert.AreEqual(0, diffed.ExitCode, diffed.Output);
        Assert.Contains("Added: 0", diffed.Output, StringComparison.Ordinal);
        Assert.Contains("Removed: 0", diffed.Output, StringComparison.Ordinal);
        Assert.Contains(expectedModified, diffed.Output, StringComparison.Ordinal);
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
