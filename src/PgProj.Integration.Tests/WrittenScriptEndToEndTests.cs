using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-165 against a real server (the user's design of 2026-11-09): the script <c>publish --output</c> writes carries its own transaction, so the server's own
/// <c>psql -f</c>, with no options, applies all of it or, when a statement fails, none of it; with <c>--concurrently</c>, run with <c>ON_ERROR_STOP</c>, the
/// statements after the <c>COMMIT</c> are not run after a failed transaction. <c>--execute</c> is covered by the other publish tests and is unchanged.
/// </summary>
[TestClass]
public sealed class WrittenScriptEndToEndTests
{
    private const string Schema = """
        CREATE SCHEMA shop;
        CREATE TABLE shop.customer (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, email text NOT NULL UNIQUE);
        CREATE TABLE shop.orders (id bigint PRIMARY KEY, customer_id bigint NOT NULL REFERENCES shop.customer (id), total numeric NOT NULL CHECK (total >= 0));
        CREATE VIEW shop.order_totals AS SELECT customer_id, sum(total) AS total FROM shop.orders GROUP BY customer_id;
        """;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task AWrittenScriptRunWithPsqlAppliesAllOfItAndConverges()
    {
        var databaseName = await CreateDatabaseAsync("g165ok");
        var directory = CreateProjectDirectory("g165ok");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(directory);
            WriteSchema(directory, Schema);
            var scriptPath = Path.Combine(directory, "deploy.sql");

            var written = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--output", scriptPath, "--lock-timeout", "5s");
            Assert.AreEqual(0, written.ExitCode, written.Output);
            Assert.DoesNotContain("not wrapped in a transaction", written.Output, "a plain script is not warned about");
            var script = await File.ReadAllTextAsync(scriptPath, TestContext.CancellationToken);
            StringAssert.StartsWith(script, "SET lock_timeout = '5s';", "the lock timeout comes before the BEGIN, for the whole session");
            Assert.IsLessThan(script.IndexOf("CREATE TABLE", StringComparison.Ordinal), script.IndexOf("BEGIN;", StringComparison.Ordinal));
            StringAssert.EndsWith(script.TrimEnd(), "COMMIT;");
            Assert.AreEqual(string.Empty, await ScalarTextAsync(connectionString, "select coalesce(to_regclass('shop.customer')::text, '')"), "writing the script runs nothing");

            var (exitCode, output) = await PostgresContainerFixture.RunPsqlFileAsync(databaseName, script);

            Assert.AreEqual(0L, exitCode, output);
            Assert.DoesNotContain("WARNING", output, "no nested or missing transaction");
            Assert.DoesNotContain("ERROR", output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task AStatementThatFailsInAWrittenScriptLeavesNothingApplied()
    {
        var databaseName = await CreateDatabaseAsync("g165fail");
        var directory = CreateProjectDirectory("g165fail", """, "postdeploy": "./post.sql" """);
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(directory);
            WriteSchema(directory, Schema);
            // The post-deploy script runs last, after every table and view is created, and fails on the server.
            File.WriteAllText(Path.Combine(directory, "post.sql"), "INSERT INTO shop.orders (id, customer_id, total) VALUES (1, 1, -1);\n");
            var scriptPath = Path.Combine(directory, "deploy.sql");
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--output", scriptPath)).ExitCode);

            // No ON_ERROR_STOP: psql goes on after the error, and the server refuses everything until the COMMIT, which rolls the transaction back.
            var (_, output) = await PostgresContainerFixture.RunPsqlFileAsync(databaseName, await File.ReadAllTextAsync(scriptPath, TestContext.CancellationToken));

            StringAssert.Contains(output, "ERROR:");
            StringAssert.Contains(output, "ROLLBACK", "psql reports the COMMIT of a failed transaction as a rollback");
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "select count(*)::text from pg_namespace where nspname = 'shop'"), "nothing was applied, not even the schema");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task AConcurrentlyScriptRunWithOnErrorStopBuildsItsIndexesAfterTheCommit()
    {
        var databaseName = await CreateDatabaseAsync("g165conc");
        var directory = CreateProjectDirectory("g165conc");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(directory);
            await ExecuteAsync(connectionString, "CREATE SCHEMA shop; CREATE TABLE shop.customer (id bigint PRIMARY KEY, email text NOT NULL); INSERT INTO shop.customer VALUES (1, 'a@example.com');");
            WriteSchema(directory, "CREATE SCHEMA shop; CREATE TABLE shop.customer (id bigint PRIMARY KEY, email text NOT NULL, name text); CREATE INDEX customer_email_idx ON shop.customer (email);");
            var scriptPath = Path.Combine(directory, "deploy.sql");

            var written = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--concurrently", "--output", scriptPath);
            Assert.AreEqual(0, written.ExitCode, written.Output);
            StringAssert.Contains(written.Output, "psql -v ON_ERROR_STOP=1 -f <file>");
            var script = await File.ReadAllTextAsync(scriptPath, TestContext.CancellationToken);
            Assert.IsLessThan(script.IndexOf(PgProj.Deploy.ConcurrentPublishPlan.Marker, StringComparison.Ordinal), script.IndexOf("COMMIT;", StringComparison.Ordinal), "the COMMIT comes before the marker");

            var (exitCode, output) = await PostgresContainerFixture.RunPsqlFileAsync(databaseName, script, "-v", "ON_ERROR_STOP=1");

            Assert.AreEqual(0L, exitCode, output);
            Assert.AreEqual("t", await ScalarTextAsync(connectionString, "select indisvalid::text::char from pg_index where indexrelid = 'shop.customer_email_idx'::regclass"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task AFailedTransactionStopsAConcurrentlyScriptBeforeItsIndexes()
    {
        var databaseName = await CreateDatabaseAsync("g165cfail");
        var directory = CreateProjectDirectory("g165cfail", """, "postdeploy": "./post.sql" """);
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(directory);
            await ExecuteAsync(connectionString, "CREATE SCHEMA shop; CREATE TABLE shop.customer (id bigint PRIMARY KEY, email text NOT NULL);");
            WriteSchema(directory, "CREATE SCHEMA shop; CREATE TABLE shop.customer (id bigint PRIMARY KEY, email text NOT NULL, name text); CREATE INDEX customer_email_idx ON shop.customer (email);");
            File.WriteAllText(Path.Combine(directory, "post.sql"), "INSERT INTO shop.customer (id, email) VALUES (1, NULL);\n");
            var scriptPath = Path.Combine(directory, "deploy.sql");
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--concurrently", "--output", scriptPath)).ExitCode);

            var (exitCode, output) = await PostgresContainerFixture.RunPsqlFileAsync(databaseName, await File.ReadAllTextAsync(scriptPath, TestContext.CancellationToken), "-v", "ON_ERROR_STOP=1");

            Assert.AreEqual(3L, exitCode, "psql's exit code when ON_ERROR_STOP stops a script: " + output);
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "select count(*)::text from pg_attribute where attrelid = 'shop.customer'::regclass and attname = 'name'"), "the transaction was rolled back");
            Assert.AreEqual(string.Empty, await ScalarTextAsync(connectionString, "select coalesce(to_regclass('shop.customer_email_idx')::text, '')"), "the index after the COMMIT was not built");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(directory);
        }
    }
}
