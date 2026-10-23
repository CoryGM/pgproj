using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-176 against a real server. The live read skipped <c>EXCLUDE</c> constraints (<c>contype = 'x'</c>), so a project with one was never seen as
/// converged: <c>diff</c> listed it as added after every publish, and the second <c>publish</c> failed with <c>42P07</c>.
/// </summary>
[TestClass]
public sealed class ExclusionConstraintEndToEndTests
{
    private const string Sql =
        """
        CREATE EXTENSION IF NOT EXISTS btree_gist;
        CREATE TABLE s.r (
            id int PRIMARY KEY,
            during tsrange,
            room int,
            label text,
            active boolean NOT NULL DEFAULT true,
            CONSTRAINT no_overlap EXCLUDE USING gist (room WITH =, during WITH &&),
            CONSTRAINT no_overlap_active EXCLUDE USING gist (room WITH =, during WITH &&) WHERE (active),
            CONSTRAINT no_same_label EXCLUDE USING gist (lower(label) WITH =) INCLUDE (room) DEFERRABLE INITIALLY DEFERRED
        );
        """;

    [TestMethod]
    public async Task ExclusionConstraintsConverge_AreChangedAndScaffolded()
    {
        var databaseName = $"g176_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g176-" + Guid.NewGuid().ToString("N"));
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g176-init-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {databaseName};");
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            var sqlFile = Path.Combine(projectDirectory, "sql", "schema.sql");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g176", "version": "1.0", "source": "./sql", "schemas": ["s"] }""");
            await File.WriteAllTextAsync(sqlFile, Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            await AssertDiffAsync(projectFile, connectionString);

            var republished = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, republished.ExitCode, "the second publish failed.\n" + republished.Output);

            // init writes both constraints into the table's file, and that project converges.
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var tableFile = Directory.EnumerateFiles(scaffoldDirectory, "r.sql", SearchOption.AllDirectories).Single();
            var tableText = await File.ReadAllTextAsync(tableFile);
            StringAssert.Contains(tableText, "no_overlap");
            StringAssert.Contains(tableText, "no_overlap_active");
            StringAssert.Contains(tableText, "EXCLUDE USING gist");
            await AssertDiffAsync(scaffoldProject, connectionString);

            // A changed constraint is seen and redeployed (a drop and an add), then converges.
            await File.WriteAllTextAsync(sqlFile, Sql.Replace("WHERE (active)", "WHERE (NOT active)", StringComparison.Ordinal));
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            StringAssert.Contains(await ScalarAsync(connectionString, "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'no_overlap_active';"), "NOT active");
            await AssertDiffAsync(projectFile, connectionString);

            // A removed one is dropped.
            var withoutSecond = string.Join(
                Environment.NewLine,
                Sql.ReplaceLineEndings().Split(Environment.NewLine).Where(static line => !line.Contains("no_overlap_active", StringComparison.Ordinal)));
            await File.WriteAllTextAsync(sqlFile, withoutSecond);
            var dropped = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, dropped.ExitCode, dropped.Output);
            Assert.AreEqual("no_overlap,no_same_label", await ScalarAsync(connectionString, "SELECT string_agg(conname, ',' ORDER BY conname) FROM pg_constraint WHERE contype = 'x';"));
            await AssertDiffAsync(projectFile, connectionString);
        }
        finally
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE);");
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
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
