using Npgsql;
using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-270 against a real server (scoped by the user on 2026-10-13). <c>publish</c> names a change that rewrites or locks a table that already exists before anything runs,
/// and <c>--lock-timeout</c> makes a publish that cannot get its lock fail fast, roll back, and name the statement, instead of waiting behind another session's transaction.
/// </summary>
[TestClass]
public sealed class LockTimeoutEndToEndTests
{
    [TestMethod]
    public async Task ALockTimeoutRollsBackAndNamesTheStatementAndTheChangeIsWarnedAbout()
    {
        var databaseName = await CreateDatabaseAsync("g270");
        var projectDirectory = CreateProjectDirectory("g270");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, "CREATE TABLE public.t (id int PRIMARY KEY, a int);\n");
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);
            await ExecuteAsync(connectionString, "INSERT INTO public.t VALUES (1, 1);");

            WriteSchema(projectDirectory, "CREATE TABLE public.t (id int PRIMARY KEY, a bigint);\nCREATE INDEX t_a ON public.t (a);\n");

            // The change is named before anything runs, in a dry run too.
            var dryRun = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run", "--lock-timeout", "2s");
            Assert.AreEqual(0, dryRun.ExitCode, dryRun.Output);
            StringAssert.Contains(dryRun.Output, "public.t: ALTER COLUMN a TYPE rewrites the table and its indexes under an ACCESS EXCLUSIVE lock");
            StringAssert.Contains(dryRun.Output, "public.t: CREATE INDEX t_a blocks writes to the table while the index is built");
            StringAssert.Contains(dryRun.Output, "SET lock_timeout = '2s';");

            // Another session holds a lock the change conflicts with.
            await using (var holder = new NpgsqlConnection(connectionString))
            {
                await holder.OpenAsync();
                await using var transaction = await holder.BeginTransactionAsync();
                await using (var hold = new NpgsqlCommand("SELECT count(*) FROM public.t;", holder, transaction))
                {
                    await hold.ExecuteScalarAsync();
                }

                var started = DateTime.UtcNow;
                var timedOut = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive", "--lock-timeout", "2s");
                var waited = DateTime.UtcNow - started;
                Assert.AreEqual(1, timedOut.ExitCode, timedOut.Output);
                StringAssert.Contains(timedOut.Output, "a lock was not granted within --lock-timeout 2s, so nothing was applied");
                StringAssert.Contains(timedOut.Output, "ALTER TABLE");
                Assert.IsLessThan(TimeSpan.FromSeconds(60), waited, "The publish gave up instead of waiting for the other session.");
                await transaction.RollbackAsync();
            }

            Assert.AreEqual("integer", await ScalarTextAsync(connectionString, "SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'public.t'::regclass AND attname = 'a'"), "Nothing was applied.");

            // With the lock free, the same publish succeeds.
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive", "--lock-timeout", "2s");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
