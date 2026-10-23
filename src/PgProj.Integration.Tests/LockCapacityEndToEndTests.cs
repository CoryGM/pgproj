using System.Globalization;
using System.Text;
using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-280 against a real server (option (b), the user's decision of 2026-10-13). A schema with more relations than the server's lock table holds failed partway through
/// <c>publish --execute</c> with "out of shared memory" (GitLab's, into a server with the defaults). <c>publish --execute</c> now refuses it before running anything unless
/// <c>--allow-lock-limit</c>; <c>--dry-run</c> only warns; a small schema says nothing.
/// </summary>
[TestClass]
public sealed class LockCapacityEndToEndTests
{
    [TestMethod]
    public async Task AScriptOverTheLockTableIsRefusedBeforeAnythingRuns()
    {
        var databaseName = await CreateDatabaseAsync("g280");
        var projectDirectory = CreateProjectDirectory("g280");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            var locks = int.Parse(
                await ScalarTextAsync(connectionString, "SELECT current_setting('max_locks_per_transaction')::int * (current_setting('max_connections')::int + current_setting('max_prepared_transactions')::int)"),
                CultureInfo.InvariantCulture);

            // A new table with a text column is three locks (the table, its TOAST table, and the TOAST index).
            var sql = new StringBuilder();
            for (var i = 0; i < locks / 3 + 50; i++)
            {
                sql.Append(CultureInfo.InvariantCulture, $"CREATE TABLE public.t{i} (id int, note text);\n");
            }

            WriteSchema(projectDirectory, sql.ToString());

            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(1, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "more than the server's lock table holds");
            StringAssert.Contains(refused.Output, "--allow-lock-limit");
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_class WHERE relname LIKE 't%' AND relnamespace = 'public'::regnamespace"), "nothing was run");

            var dryRun = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.AreEqual(0, dryRun.ExitCode, dryRun.Output);
            StringAssert.Contains(dryRun.Output, "Warning: the script holds about");

            WriteSchema(projectDirectory, "CREATE TABLE public.small (id int, note text);\n");
            var small = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, small.ExitCode, small.Output);
            Assert.DoesNotContain("lock table", small.Output, StringComparison.Ordinal);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
