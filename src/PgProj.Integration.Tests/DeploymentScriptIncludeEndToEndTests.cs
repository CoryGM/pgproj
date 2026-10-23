using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-124 against a real server: a post-deployment script that pulls a file from the <c>Scripts</c> folder in with <c>:r</c> runs that file's
/// statements, the folder is never read as schema (so <c>diff</c> stays <c>0/0/0</c> and warns of nothing), and a syntax error in an
/// included file stops <c>publish --execute</c> with the file and line named. Before, every <c>*.sql</c> under <c>source</c> was schema and the
/// <c>:r</c> line was sent to the server as it was.
/// </summary>
[TestClass]
public sealed class DeploymentScriptIncludeEndToEndTests
{
    [TestMethod]
    public async Task AnIncludedScriptRunsOnPublishAndTheScriptsFolderIsNotSchema()
    {
        var databaseName = $"g124_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g124-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql", "Scripts"));

        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {databaseName};");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g124", "version": "1.0", "source": "./sql", "postdeploy": "./sql/post.sql" }""");
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "schema.sql"), "CREATE TABLE public.t (id integer PRIMARY KEY, note text);\n");
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "post.sql"), "-- seed data\n:r ./Scripts/seed.sql\n");
            await File.WriteAllTextAsync(
                Path.Combine(projectDirectory, "sql", "Scripts", "seed.sql"),
                "INSERT INTO public.t (id, note) VALUES (1, 'one') ON CONFLICT (id) DO UPDATE SET note = EXCLUDED.note;\n:r ./more.sql\n");
            await File.WriteAllTextAsync(
                Path.Combine(projectDirectory, "sql", "Scripts", "more.sql"),
                "INSERT INTO public.t (id, note) VALUES (2, 'two') ON CONFLICT (id) DO UPDATE SET note = EXCLUDED.note;\n");

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            StringAssert.Contains(built.Output, "SQL files: 1");
            StringAssert.Contains(built.Output, "No diagnostics.");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("1:one|2:two", await ScalarAsync(connectionString, "SELECT string_agg(id || ':' || note, '|' ORDER BY id) FROM public.t;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A second publish runs the (repeatable) script again and changes nothing else.
            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, again.ExitCode, again.Output);
            Assert.AreEqual("2", await ScalarAsync(connectionString, "SELECT count(*)::text FROM public.t;"));
        }
        finally
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE);");
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task ASyntaxErrorInAnIncludedScriptStopsPublishAndNamesTheFileAndLine()
    {
        var databaseName = $"g124e_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g124e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql", "Scripts"));

        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {databaseName};");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g124", "version": "1.0", "source": "./sql", "postdeploy": "./sql/post.sql" }""");
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "schema.sql"), "CREATE TABLE public.t (id integer PRIMARY KEY);\n");
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "post.sql"), ":r ./Scripts/seed.sql\n");
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "Scripts", "seed.sql"), "-- seed\n\nINSERT INTO public.t (id) VALUES (1,, 2);\n");

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(1, built.ExitCode, built.Output);
            StringAssert.Contains(built.Output, $"{Path.Combine("Scripts", "seed.sql")}:3:");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreNotEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("0", await ScalarAsync(connectionString, "SELECT count(*)::text FROM pg_tables WHERE tablename = 't';"), "Nothing was deployed.");
        }
        finally
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE);");
            TryDeleteDirectory(projectDirectory);
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
