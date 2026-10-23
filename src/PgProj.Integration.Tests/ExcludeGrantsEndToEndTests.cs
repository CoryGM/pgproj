using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-87: proves the symmetry the whole design depends on. A project with <c>excludeGrants: true</c> must never see
/// a live grant it doesn't declare - not as a phantom "Removed", and (after G-80) never as something
/// <c>publish --execute</c> would need <c>--allow-destructive</c> to leave alone. <c>--exclude-grants false</c> on
/// the CLI must be able to force the same live grant back into view, symmetrically, so it becomes a genuine,
/// correctly-gated removal.
/// </summary>
[TestClass]
public sealed class ExcludeGrantsEndToEndTests
{
    [TestMethod]
    public async Task ExcludeGrants_MakesAnUndeclaredLiveGrantInvisible_UntilTheCliOverridesItBackOn()
    {
        var databaseName = $"g87_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g87-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await CreateDatabaseAsync(databaseName);
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            await File.WriteAllTextAsync(
                projectFile,
                """{ "name": "g87", "version": "1.0", "source": "./sql", "excludeGrants": true }""");
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "schema.sql"), "CREATE TABLE public.widgets (id bigint PRIMARY KEY);");

            // 1. Deploy the table. The project never declares a grant, so nothing to exclude yet either way.
            var first = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, first.ExitCode, "Initial publish failed.\n" + first.Output);

            // 2. A grant appears live that this project doesn't manage (PUBLIC needs no role setup).
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var grant = new NpgsqlCommand("GRANT SELECT ON public.widgets TO PUBLIC;", connection);
                await grant.ExecuteNonQueryAsync();
            }

            // 3. With excludeGrants: true (from pgproj.json, no CLI flag needed), the grant is fully invisible:
            //    not a "Removed" candidate, and publish --execute needs no --allow-destructive to leave it alone.
            var excludedDiff = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            Assert.AreEqual(0, excludedDiff.ExitCode, excludedDiff.Output);
            StringAssert.Contains(excludedDiff.Output, "Removed: 0");

            var excludedPublish = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, excludedPublish.ExitCode, "Publish should not need --allow-destructive while the grant is excluded.\n" + excludedPublish.Output);
            Assert.IsTrue(await HasPublicSelectOnWidgetsAsync(connectionString), "The excluded grant must survive an excluded publish untouched.");

            // 4. --exclude-grants false overrides pgproj.json for this run: the same grant becomes a real, visible
            //    removal candidate, gated by G-80 like any other revoke.
            var includedDiff = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString, "--exclude-grants", "false");
            Assert.AreEqual(0, includedDiff.ExitCode, includedDiff.Output);
            StringAssert.Contains(includedDiff.Output, "Removed: 1");

            var refusedPublish = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--exclude-grants", "false");
            Assert.AreEqual(1, refusedPublish.ExitCode, "Publish should refuse the now-visible revoke without --allow-destructive.\n" + refusedPublish.Output);
            Assert.Contains("grant revoked", refusedPublish.Output, StringComparison.Ordinal);
            Assert.IsTrue(await HasPublicSelectOnWidgetsAsync(connectionString), "The grant must survive a refused publish.");

            var allowedPublish = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive", "--exclude-grants", "false");
            Assert.AreEqual(0, allowedPublish.ExitCode, "Publish with --allow-destructive failed.\n" + allowedPublish.Output);
            Assert.IsFalse(await HasPublicSelectOnWidgetsAsync(connectionString), "The grant must actually be revoked once included and allowed.");
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

    private static async Task<bool> HasPublicSelectOnWidgetsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT has_table_privilege('public', 'public.widgets', 'SELECT');", connection);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(params string[] args)
    {
        var context = PostgresContainerFixture.CreateCliContext();
        var root = new RootCommand("pgproj") { BuildCommand.Create(context), DiffCommand.Create(context), PublishCommand.Create(context) };

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
}
