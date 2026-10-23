using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-86 against a real server: every PostgreSQL database automatically grants <c>USAGE ON SCHEMA public TO
/// PUBLIC</c> before any project ever runs, whether or not the project mentions it. A project that uses <c>public</c>
/// must not have this default proposed for removal - and, since a <c>REVOKE</c> is not classified as destructive
/// (G-80), an undetected phantom here would mean <c>publish --execute</c> silently revoking baseline schema access
/// on the project's very first publish. Uses a fresh per-test database, since <c>public</c> is shared by every test
/// on the default connection.
/// </summary>
[TestClass]
public sealed class DefaultPublicSchemaGrantEndToEndTests
{
    [TestMethod]
    public async Task AProjectThatNeverMentionsThePublicSchemaGrantConvergesWithoutRevokingIt()
    {
        var databaseName = $"g86_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g86-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await CreateDatabaseAsync(databaseName);
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g86", "version": "1.0", "source": "./sql" }""");
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "schema.sql"), "CREATE TABLE public.widgets (id bigint PRIMARY KEY);");

            // 1. diff against the fresh (otherwise empty) database, which already carries PostgreSQL's automatic
            //    default grant, proposes adding only the table - not removing the grant this project never wrote.
            var firstDiff = await RunAsync(DiffCommand.Create, "diff", "--project", projectFile, "--target-connection", connectionString);
            Assert.AreEqual(0, firstDiff.ExitCode, firstDiff.Output);
            Assert.Contains("Removed: 0", firstDiff.Output, StringComparison.Ordinal);

            // 2. publish --execute succeeds and does not revoke PUBLIC's USAGE on public.
            var published = await RunAsync(PublishCommand.Create, "publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.IsTrue(await HasPublicUsageOnPublicSchemaAsync(connectionString), "PUBLIC's default USAGE on the public schema must survive publish.");

            // 3. a second diff re-diffs empty: the grant is not perpetually flagged.
            var secondDiff = await RunAsync(DiffCommand.Create, "diff", "--project", projectFile, "--target-connection", connectionString);
            Assert.AreEqual(0, secondDiff.ExitCode, secondDiff.Output);
            Assert.Contains("Added: 0", secondDiff.Output, StringComparison.Ordinal);
            Assert.Contains("Removed: 0", secondDiff.Output, StringComparison.Ordinal);
            Assert.Contains("Modified: 0", secondDiff.Output, StringComparison.Ordinal);
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

    [TestMethod]
    public async Task AProjectThatExplicitlyDeclaresThePublicSchemaGrantAlsoConverges()
    {
        var databaseName = $"g86_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g86-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await CreateDatabaseAsync(databaseName);
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g86", "version": "1.0", "source": "./sql" }""");
            await File.WriteAllTextAsync(
                Path.Combine(projectDirectory, "sql", "schema.sql"),
                "CREATE TABLE public.widgets (id bigint PRIMARY KEY); GRANT USAGE ON SCHEMA public TO PUBLIC;");

            var published = await RunAsync(PublishCommand.Create, "publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.IsTrue(await HasPublicUsageOnPublicSchemaAsync(connectionString));

            var diffed = await RunAsync(DiffCommand.Create, "diff", "--project", projectFile, "--target-connection", connectionString);
            Assert.AreEqual(0, diffed.ExitCode, diffed.Output);
            Assert.Contains("Added: 0", diffed.Output, StringComparison.Ordinal);
            Assert.Contains("Removed: 0", diffed.Output, StringComparison.Ordinal);
            Assert.Contains("Modified: 0", diffed.Output, StringComparison.Ordinal);
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

    private static async Task<bool> HasPublicUsageOnPublicSchemaAsync(string connectionString)
    {
        // grantee = 0 is PostgreSQL's own sentinel for the PUBLIC pseudo-role in an ACL, the same check
        // LiveDatabaseModelLoader.Grants.cs's ReadSchemaGrantsAsync itself uses.
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1 FROM pg_catalog.pg_namespace ns
                CROSS JOIN LATERAL pg_catalog.aclexplode(ns.nspacl) acl
                WHERE ns.nspname = 'public' AND acl.grantee = 0 AND acl.privilege_type = 'USAGE'
            );
            """,
            connection);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
