using System.CommandLine;
using Npgsql;
using PgProj.Cli;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-35: runs the real <c>pgproj publish --execute</c> command against a disposable database to prove it refuses
/// a script that can discard data (a dropped table, a dropped column) unless <c>--allow-destructive</c> is
/// passed, and that nothing is actually dropped when it refuses.
/// </summary>
[TestClass]
public sealed class DestructiveOperationEndToEndTests
{
    private const string InitialSql = """
        CREATE EXTENSION plpgsql SCHEMA pg_catalog;

        CREATE TABLE public.customers (
            id bigint PRIMARY KEY,
            email text NOT NULL,
            legacy_note text
        );

        CREATE TABLE public.orders (
            id bigint PRIMARY KEY,
            customer_id bigint REFERENCES public.customers,
            total numeric(10,2) NOT NULL
        );
        """;

    // Drops public.orders entirely and drops customers.legacy_note.
    private const string ShrunkSql = """
        CREATE EXTENSION plpgsql SCHEMA pg_catalog;

        CREATE TABLE public.customers (
            id bigint PRIMARY KEY,
            email text NOT NULL
        );
        """;

    [TestMethod]
    public async Task PublishExecute_RefusesDestructiveChanges_ThenSucceedsWithAllowDestructive()
    {
        var databaseName = $"g35_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g35-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await CreateDatabaseAsync(databaseName);
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g35", "version": "1.0", "source": "./sql" }""");

            // 1. Publish the initial schema from empty.
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "schema.sql"), InitialSql);
            var first = await PublishAsync(projectFile, connectionString, allowDestructive: false);
            Assert.AreEqual(0, first.ExitCode, "Initial publish failed.\n" + first.Output);

            // 2. Shrink the project (drop a table, drop a column). Without --allow-destructive, --execute must refuse
            //    and leave the database untouched.
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "schema.sql"), ShrunkSql);
            var refused = await PublishAsync(projectFile, connectionString, allowDestructive: false);
            Assert.AreEqual(1, refused.ExitCode, "Publish should have been refused.\n" + refused.Output);
            Assert.Contains("can discard data", refused.Output, StringComparison.Ordinal);
            Assert.Contains("--allow-destructive", refused.Output, StringComparison.Ordinal);
            Assert.AreEqual(1L, await ScalarAsync(connectionString, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'orders';"));
            Assert.AreEqual(1L, await ScalarAsync(connectionString, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'customers' AND column_name = 'legacy_note';"));

            // 3. With --allow-destructive, the same publish runs.
            var allowed = await PublishAsync(projectFile, connectionString, allowDestructive: true);
            Assert.AreEqual(0, allowed.ExitCode, "Publish with --allow-destructive failed.\n" + allowed.Output);
            Assert.AreEqual(0L, await ScalarAsync(connectionString, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'orders';"));
            Assert.AreEqual(0L, await ScalarAsync(connectionString, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'customers' AND column_name = 'legacy_note';"));

            // 4. It converges: a third publish (still --allow-destructive; nothing destructive is left to allow) is a no-op.
            var converged = await PublishAsync(projectFile, connectionString, allowDestructive: true);
            Assert.AreEqual(0, converged.ExitCode, "Final publish failed.\n" + converged.Output);
            foreach (var keyword in new[] { "CREATE ", "ALTER ", "DROP " })
            {
                Assert.IsFalse(converged.Output.Contains(keyword, StringComparison.Ordinal), $"Expected the final publish to change nothing but the script contained '{keyword.Trim()}':\n{converged.Output}");
            }
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

    /// <summary>
    /// G-80: a grant the project stops declaring is an access-loss event, the same category G-35 already gates -
    /// <c>publish --execute</c> must refuse the REVOKE unless <c>--allow-destructive</c> is given, and must leave
    /// the grant untouched when it refuses. Uses <c>PUBLIC</c> as the grantee so the test needs no extra role.
    /// </summary>
    [TestMethod]
    public async Task PublishExecute_RefusesRevokedGrant_ThenSucceedsWithAllowDestructive()
    {
        var databaseName = $"g80_{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g80-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(projectDirectory, "sql"));

        await CreateDatabaseAsync(databaseName);
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(PostgresContainerFixture.ConnectionString) { Database = databaseName }.ConnectionString;
            var projectFile = Path.Combine(projectDirectory, "pgproj.json");
            await File.WriteAllTextAsync(projectFile, """{ "name": "g80", "version": "1.0", "source": "./sql" }""");

            const string WithGrant = "CREATE TABLE public.reports (id bigint PRIMARY KEY);\nGRANT SELECT ON public.reports TO PUBLIC;";
            const string WithoutGrant = "CREATE TABLE public.reports (id bigint PRIMARY KEY);";

            // 1. Publish the table with its grant.
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "schema.sql"), WithGrant);
            var first = await PublishAsync(projectFile, connectionString, allowDestructive: false);
            Assert.AreEqual(0, first.ExitCode, "Initial publish failed.\n" + first.Output);
            Assert.IsTrue(await HasPublicSelectOnReportsAsync(connectionString), "Grant should exist after initial publish.");

            // 2. Drop the grant from the project. Without --allow-destructive, --execute must refuse and leave the grant alone.
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sql", "schema.sql"), WithoutGrant);
            var refused = await PublishAsync(projectFile, connectionString, allowDestructive: false);
            Assert.AreEqual(1, refused.ExitCode, "Publish should have been refused.\n" + refused.Output);
            Assert.Contains("can discard data", refused.Output, StringComparison.Ordinal);
            Assert.Contains("--allow-destructive", refused.Output, StringComparison.Ordinal);
            Assert.Contains("grant revoked", refused.Output, StringComparison.Ordinal);
            Assert.IsTrue(await HasPublicSelectOnReportsAsync(connectionString), "Grant must survive a refused publish.");

            // 3. With --allow-destructive, the REVOKE runs.
            var allowed = await PublishAsync(projectFile, connectionString, allowDestructive: true);
            Assert.AreEqual(0, allowed.ExitCode, "Publish with --allow-destructive failed.\n" + allowed.Output);
            Assert.IsFalse(await HasPublicSelectOnReportsAsync(connectionString), "Grant must be revoked once allowed.");
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

    private static async Task<bool> HasPublicSelectOnReportsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT has_table_privilege('public', 'public.reports', 'SELECT');", connection);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<(int ExitCode, string Output)> PublishAsync(string projectFile, string connectionString, bool allowDestructive)
    {
        var context = PostgresContainerFixture.CreateCliContext();
        var root = new RootCommand("pgproj") { PublishCommand.Create(context) };

        var args = new List<string> { "publish", "--project", projectFile, "--target-connection", connectionString, "--execute" };
        if (allowDestructive)
        {
            args.Add("--allow-destructive");
        }

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(output);
        try
        {
            var exitCode = await root.Parse(args.ToArray()).InvokeAsync();
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

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
