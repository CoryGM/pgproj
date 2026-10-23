using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-85: <c>diff</c>/<c>publish</c> scope their live database read to the project's <c>schemas</c> list (when
/// populated), so a live object belonging to another project's schema is never read at all - not modeled, not
/// diffed, never a removal candidate. Before this, <c>schemas</c> was read by no CLI command (G-83 was the first,
/// for build-time diagnostic severity only); <c>diff</c>/<c>publish</c> always compared against every schema the
/// database exposed.
/// </summary>
[TestClass]
public sealed class LiveScanScopeEndToEndTests
{
    // Neither schema is "public": public's own automatic default grant (GRANT USAGE ON SCHEMA public TO PUBLIC,
    // present on every database whether or not any project ever wrote it) is a separate, pre-existing phantom
    // unrelated to this gap (registered as G-86) and would otherwise show up as a spurious removal here.
    private const string ProjectSql = """
        CREATE TABLE app.widgets (
            id bigint PRIMARY KEY
        );

        CREATE TABLE reporting.orders (
            id bigint PRIMARY KEY
        );
        """;

    [TestMethod]
    public async Task AnObjectInAnUndeclaredSchemaIsInvisibleToDiffAndPublish_ButTheProjectsOwnUnlistedSchemaStillWorks()
    {
        var databaseName = $"g85_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g85-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await CreateDatabaseAsync(databaseName);
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;

            // Another project already owns this schema and table; this project never declares it anywhere.
            await ExecuteAsync(connectionString, "CREATE SCHEMA other_team; CREATE TABLE other_team.customers (id bigint PRIMARY KEY);");

            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            var scopedProjectJson = """{ "name": "g85", "version": "1.0", "source": "./sql", "schemas": ["app"] }""";
            await File.WriteAllTextAsync(projectFile, scopedProjectJson);
            // The project only lists "app" in schemas, but its own SQL also declares a table in "reporting" - the
            // safety net (CliContext.GetLiveScanSchemas) must widen the live scan to include it anyway.
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "schema.sql"), ProjectSql);

            // 1. Before anything is published: diff proposes adding only this project's own two tables (each with
            //    its primary key constraint and implicit index, 6 added objects total) and nothing else -
            //    other_team.customers is not proposed for removal, despite existing live and not being declared
            //    anywhere in this project.
            var firstDiff = await RunDiffAsync(projectFile, connectionString);
            Assert.AreEqual(0, firstDiff.ExitCode, firstDiff.Output);
            Assert.Contains("Added: 6", firstDiff.Output, StringComparison.Ordinal);
            Assert.Contains("Removed: 0", firstDiff.Output, StringComparison.Ordinal);

            // 2. publish --execute creates both of this project's tables and leaves other_team.customers alone.
            var published = await RunAsync(PublishCommand.Create, "publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual(1L, await ScalarAsync(connectionString, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'app' AND table_name = 'widgets';"));
            Assert.AreEqual(1L, await ScalarAsync(connectionString, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'reporting' AND table_name = 'orders';"));
            Assert.AreEqual(1L, await ScalarAsync(connectionString, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'other_team' AND table_name = 'customers';"));

            // 3. diff re-diffs empty: the project's own table in an unlisted schema ("reporting") converged
            //    correctly - the safety net means it was never invisible to its own diff.
            var secondDiff = await RunDiffAsync(projectFile, connectionString);
            Assert.AreEqual(0, secondDiff.ExitCode, secondDiff.Output);
            Assert.Contains("Added: 0", secondDiff.Output, StringComparison.Ordinal);
            Assert.Contains("Removed: 0", secondDiff.Output, StringComparison.Ordinal);
            Assert.Contains("Modified: 0", secondDiff.Output, StringComparison.Ordinal);

            // 4. Contrast: with schemas left empty (the pre-G-85, and still-default, polarity), diff goes back to
            //    comparing against every schema the database exposes, so other_team.customers (its table, primary
            //    key constraint, and implicit unique index) now does show up as a removal candidate - proving the
            //    earlier invisibility was this project's own schemas list at work, not some other loader change.
            await File.WriteAllTextAsync(projectFile, """{ "name": "g85", "version": "1.0", "source": "./sql" }""");
            var unscopedDiff = await RunDiffAsync(projectFile, connectionString);
            Assert.AreEqual(0, unscopedDiff.ExitCode, unscopedDiff.Output);
            Assert.DoesNotContain("Removed: 0", unscopedDiff.Output, StringComparison.Ordinal);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
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

    private static Task<(int ExitCode, string Output)> RunDiffAsync(string projectFile, string connectionString)
        => RunAsync(DiffCommand.Create, "diff", "--project", projectFile, "--target-connection", connectionString);

    private static async Task<(int ExitCode, string Output)> RunAsync(Func<CliContext, Command> createCommand, params string[] args)
    {
        var context = PostgresContainerFixture.CreateCliContext();
        var root = new RootCommand("pgproj") { createCommand(context) };

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

    private static async Task CreateDatabaseAsync(string databaseName)
    {
        await using var connection = new NpgsqlConnection(PostgresContainerFixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {databaseName};", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(string databaseName)
    {
        await using var connection = new NpgsqlConnection(PostgresContainerFixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE);", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
