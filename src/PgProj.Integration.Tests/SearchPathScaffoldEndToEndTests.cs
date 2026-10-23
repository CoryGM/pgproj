using Npgsql;
using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-251 against a real server: functions created in a session whose <c>search_path</c> includes their schema use unqualified names that a default-path project cannot find. <c>init</c> writes
/// the <c>searchPath</c> they need, the scaffold builds, and publishing it makes the same functions in another database.
/// </summary>
[TestClass]
public sealed class SearchPathScaffoldEndToEndTests
{
    [TestMethod]
    public async Task InitWritesTheSearchPathTheFunctionsNeedAndTheScaffoldPublishes()
    {
        var databaseName = await CreateDatabaseAsync("g251");
        var secondDatabase = await CreateDatabaseAsync("g251b");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g251-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                foreach (var sql in new[]
                {
                    "CREATE SCHEMA temp_func_test",
                    "SET search_path TO temp_func_test, public",
                    "CREATE FUNCTION voidtest1(a int) RETURNS int LANGUAGE sql AS 'SELECT a + 1'",
                    "CREATE FUNCTION voidtest2(a int, b int) RETURNS int LANGUAGE sql AS 'SELECT voidtest1(a) + voidtest1(b)'",
                    "CREATE TABLE ddl_test (f1 int)",
                    "CREATE FUNCTION cnt() RETURNS bigint LANGUAGE sql AS 'SELECT count(*) FROM ddl_test'",
                })
                {
                    await using var command = new NpgsqlCommand(sql, connection);
                    await command.ExecuteNonQueryAsync();
                }
            }

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            StringAssert.Contains(scaffolded.Output, "searchPath");
            var projectJson = await File.ReadAllTextAsync(scaffoldProject);
            StringAssert.Contains(projectJson, "\"temp_func_test\"");
            StringAssert.Contains(projectJson, "\"public\"");

            var built = await RunAsync("build", "--project", scaffoldProject);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            StringAssert.Contains(built.Output, "No diagnostics.");

            var secondConnection = ConnectionStringFor(secondDatabase);
            var published = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publishing the scaffold failed.\n" + published.Output);
            Assert.AreEqual("3", await ScalarTextAsync(secondConnection, "SELECT count(*) FROM pg_proc WHERE pronamespace = 'temp_func_test'::regnamespace"));
            await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);
        }
        finally
        {
            await DropDatabaseAsync(secondDatabase);
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }

    [TestMethod]
    public async Task ADatabaseThatNeedsNoSearchPathGetsNone()
    {
        var databaseName = await CreateDatabaseAsync("g251n");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g251n-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("CREATE FUNCTION public.f(a int) RETURNS int LANGUAGE sql AS 'SELECT a'", connection);
                await command.ExecuteNonQueryAsync();
            }

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            Assert.IsFalse((await File.ReadAllTextAsync(scaffoldProject)).Contains("searchPath", StringComparison.Ordinal));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
