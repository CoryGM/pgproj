using Npgsql;
using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-279 against a real server. A statement that failed in <c>publish --execute</c> was reported without the statement, and when rolling
/// back failed too (a broken connection, or the server out of shared memory), the rollback's exception replaced the server's error, so the
/// user saw only <c>Publish failed: NpgsqlTransaction</c>. Now the server's error is kept, with the statement.
/// </summary>
[TestClass]
public sealed class FailedStatementEndToEndTests
{
    [TestMethod]
    public async Task AFailedStatementIsNamedAndNothingIsApplied()
    {
        var databaseName = await CreateDatabaseAsync("g279");
        var projectDirectory = CreateProjectDirectory("g279");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, "CREATE TABLE public.customer (id int PRIMARY KEY, email text);\n");
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);
            await ExecuteAsync(connectionString, "INSERT INTO public.customer VALUES (1, NULL);");

            WriteSchema(projectDirectory, "CREATE TABLE public.customer (id int PRIMARY KEY, email text NOT NULL, added int);\n");
            var failed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");

            Assert.AreEqual(1, failed.ExitCode, failed.Output);
            StringAssert.Contains(failed.Output, "Publish failed: 23502: column \"email\" of relation \"customer\" contains null values");
            StringAssert.Contains(failed.Output, "The statement that failed: ALTER TABLE");
            StringAssert.Contains(failed.Output, "SET NOT NULL");
            StringAssert.Contains(failed.Output, "Nothing was applied: the script was rolled back.");
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_attribute WHERE attrelid = 'public.customer'::regclass AND attname = 'added'"));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task TheServersErrorIsKeptWhenRollingBackFailsToo()
    {
        var databaseName = await CreateDatabaseAsync("g279b");
        var projectDirectory = CreateProjectDirectory("g279b");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, "CREATE TABLE public.t (id int PRIMARY KEY, a int);\n");
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);
            WriteSchema(projectDirectory, "CREATE TABLE public.t (id int PRIMARY KEY, a int);\nCREATE INDEX t_a ON public.t (a);\n");

            // Another session holds a lock, so publish waits on its CREATE INDEX; a third ends publish's session while it waits. The statement
            // fails with 57P01 and the connection is gone, so rolling back fails as well.
            await using var holder = new NpgsqlConnection(connectionString);
            await holder.OpenAsync();
            await using var transaction = await holder.BeginTransactionAsync();
            await using (var hold = new NpgsqlCommand("LOCK TABLE public.t IN ROW EXCLUSIVE MODE;", holder, transaction))
            {
                await hold.ExecuteNonQueryAsync();
            }

            var publishing = RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            var terminated = false;
            for (var attempt = 0; attempt < 120 && !terminated; attempt++)
            {
                await Task.Delay(500);
                terminated = await ScalarTextAsync(connectionString, $"""
                    SELECT coalesce(bool_or(pg_terminate_backend(pid)), false)::text FROM pg_stat_activity
                    WHERE datname = '{databaseName}' AND pid <> pg_backend_pid() AND wait_event_type = 'Lock' AND query ILIKE '%CREATE INDEX%t_a%'
                    """) == "true";
            }

            var failed = await publishing;
            await transaction.RollbackAsync();

            Assert.IsTrue(terminated, "publish never waited on the lock.\n" + failed.Output);
            Assert.AreEqual(1, failed.ExitCode, failed.Output);
            StringAssert.Contains(failed.Output, "57P01");
            StringAssert.Contains(failed.Output, "The statement that failed: CREATE INDEX \"t_a\" ON \"public\".\"t\"");
            Assert.DoesNotContain("NpgsqlTransaction", failed.Output);
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_class WHERE relname = 't_a'"));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
