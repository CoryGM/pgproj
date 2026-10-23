using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-169 against a real server, on every version: <c>publish --comments-only</c> sets, changes, and clears the comments of objects the database has, skips (with a
/// warning that names it) a comment on a table or column it does not have, deploys nothing else, and leaves the predeploy and postdeploy scripts out.
/// </summary>
[TestClass]
public sealed class CommentsOnlyEndToEndTests
{
    private const string Before = """
        CREATE SCHEMA app;
        CREATE TABLE app.customers (id bigint PRIMARY KEY, name text);
        COMMENT ON COLUMN app.customers.name IS 'Old name comment.';
        """;

    private const string After = """
        CREATE SCHEMA app;
        CREATE TABLE app.customers (id bigint PRIMARY KEY, name text, nickname text);
        CREATE TABLE app.orders (id bigint PRIMARY KEY);
        CREATE INDEX customers_name ON app.customers (name);
        COMMENT ON TABLE app.customers IS 'People we sell to.';
        COMMENT ON COLUMN app.customers.name IS 'The name.';
        COMMENT ON TABLE app.orders IS 'Not in the database yet either.';
        COMMENT ON COLUMN app.customers.nickname IS 'Added with its comment.';
        """;

    private static Task<string> CommentAsync(string connectionString, string sql)
        => ScalarTextAsync(connectionString, $"select coalesce(({sql}), '<none>')");

    [TestMethod]
    public async Task OnlyTheCommentsOfWhatTheDatabaseHasAreDeployedAndTheRestIsSkippedWithWarnings()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            File.WriteAllText(Path.Combine(directory, "post.sql"), "CREATE TABLE app.from_postdeploy (id int);");
            File.WriteAllText(Path.Combine(directory, "pgproj.json"), """{ "name": "g169", "version": "1.0", "source": "./sql", "postdeploy": "./post.sql" }""");
            var project = ProjectFile(directory);

            var run = await RunAsync("publish", "--project", project, "--target-connection", connectionString, "--comments-only", "--execute");

            Assert.AreEqual(0, run.ExitCode, run.Output);
            StringAssert.Contains(run.Output, "--comments-only: deploying");
            StringAssert.Contains(run.Output, "skipped a comment");
            StringAssert.Contains(run.Output, "COMMENT ON TABLE \"app\".\"orders\"");
            StringAssert.Contains(run.Output, "COMMENT ON COLUMN \"app\".\"customers\".\"nickname\"");
            Assert.AreEqual("People we sell to.", await CommentAsync(connectionString, "select obj_description('app.customers'::regclass, 'pg_class')"));
            Assert.AreEqual("The name.", await CommentAsync(connectionString, "select col_description('app.customers'::regclass, 2)"));

            // Nothing else was deployed.
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "select count(*) from information_schema.columns where table_schema = 'app' and table_name = 'customers' and column_name = 'nickname'"));
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "select count(*) from pg_tables where schemaname = 'app' and tablename in ('orders', 'from_postdeploy')"));
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "select count(*) from pg_indexes where schemaname = 'app' and indexname = 'customers_name'"));
        });
    }

    [TestMethod]
    public async Task AFullPublishAfterwardsCreatesTheRestAndConvergesWithTheirComments()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = ProjectFile(directory);
            Assert.AreEqual(0, (await RunAsync("publish", "--project", project, "--target-connection", connectionString, "--comments-only", "--execute")).ExitCode);

            var full = await RunAsync("publish", "--project", project, "--target-connection", connectionString, "--execute");

            Assert.AreEqual(0, full.ExitCode, full.Output);
            Assert.AreEqual("Added with its comment.", await CommentAsync(connectionString, "select col_description('app.customers'::regclass, 3)"));
            Assert.AreEqual("Not in the database yet either.", await CommentAsync(connectionString, "select obj_description('app.orders'::regclass, 'pg_class')"));
            await AssertDiffIsEmptyAsync(project, connectionString);
        });
    }

    [TestMethod]
    public async Task AClearedCommentIsClearedAndTheScriptIsPrintedWithoutExecute()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = ProjectFile(directory);
            WriteSchema(directory, Edit(After, "COMMENT ON COLUMN app.customers.name IS 'The name.';", "COMMENT ON COLUMN app.customers.name IS NULL;"));

            var printed = await RunAsync("publish", "--project", project, "--target-connection", connectionString, "--comments-only");

            Assert.AreEqual(0, printed.ExitCode, printed.Output);
            StringAssert.Contains(printed.Output, "DO $pgproj_comments_only$");
            Assert.IsFalse(printed.Output.Contains("CREATE TABLE", StringComparison.Ordinal), printed.Output);
            Assert.AreEqual("Old name comment.", await CommentAsync(connectionString, "select col_description('app.customers'::regclass, 2)"), "Nothing ran.");

            var run = await RunAsync("publish", "--project", project, "--target-connection", connectionString, "--comments-only", "--execute");

            Assert.AreEqual(0, run.ExitCode, run.Output);
            Assert.AreEqual("<none>", await CommentAsync(connectionString, "select col_description('app.customers'::regclass, 2)"));
        });
    }

    [TestMethod]
    public async Task TheWrittenScriptRunsUnderPsqlStyleExecutionAndSkipsTheMissingObjects()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = ProjectFile(directory);
            var scriptPath = Path.Combine(directory, "comments.sql");

            var run = await RunAsync("publish", "--project", project, "--target-connection", connectionString, "--comments-only", "--output", scriptPath, "--quiet");

            Assert.AreEqual(0, run.ExitCode, run.Output);
            await ExecuteAsync(connectionString, File.ReadAllText(scriptPath));
            Assert.AreEqual("People we sell to.", await CommentAsync(connectionString, "select obj_description('app.customers'::regclass, 'pg_class')"));
        });
    }

    private static async Task WithDatabaseAsync(Func<string, string, Task> test)
    {
        var databaseName = await CreateDatabaseAsync("g169");
        var directory = CreateProjectDirectory("g169");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, Before);
            WriteSchema(directory, After);
            await test(connectionString, directory);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(directory);
        }
    }
}
