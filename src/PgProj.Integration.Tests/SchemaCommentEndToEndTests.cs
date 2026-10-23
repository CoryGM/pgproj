using System.Text.Json;
using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-160 against a real server, on every version: a schema's comment is read from the database, scaffolded by <c>init</c> (the <c>public</c> schema's default comment is not),
/// compared with the project's, set, changed, and cleared by <c>publish</c>, left alone when the project does not state one, created with a new schema, and shown by <c>document</c>.
/// </summary>
[TestClass]
public sealed class SchemaCommentEndToEndTests
{
    private const string Sql = """
        CREATE SCHEMA app;
        CREATE SCHEMA audit;
        CREATE TABLE app.customers (id bigint PRIMARY KEY);
        CREATE TABLE audit.entry (id bigint PRIMARY KEY);
        COMMENT ON SCHEMA app IS $$The application's tables.

        @owner: Platform
        @business-name: Application$$;
        """;

    private static Task<string> CommentOfAsync(string connectionString, string schema)
        => ScalarTextAsync(connectionString, $"select coalesce(obj_description(oid, 'pg_namespace'), '<none>') from pg_namespace where nspname = '{schema}'");

    [TestMethod]
    public async Task InitScaffoldsTheCommentAndTheProjectConvergesWithTheDatabase()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = Path.Combine(directory, "pgproj.json");
            var run = await RunAsync("init", "--source-connection", connectionString, "--project", project);

            Assert.AreEqual(0, run.ExitCode, run.Output);
            var appFile = File.ReadAllText(Path.Combine(directory, "sql", "Schema", "app", "app.sql"));
            StringAssert.Contains(appFile, "COMMENT ON SCHEMA app IS ");
            StringAssert.Contains(appFile, "The application''s tables.");
            Assert.IsFalse(File.ReadAllText(Path.Combine(directory, "sql", "Schema", "audit", "audit.sql")).Contains("COMMENT ON SCHEMA", StringComparison.Ordinal), "audit has no comment.");
            Assert.IsFalse(File.ReadAllText(Path.Combine(directory, "sql", "Schema", "public", "public.sql")).Contains("COMMENT ON SCHEMA", StringComparison.Ordinal), "public's default comment says nothing about this database.");
            await AssertDiffIsEmptyAsync(project, connectionString);
        });
    }

    [TestMethod]
    public async Task APublishSetsChangesAndClearsAStatedComment()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = Path.Combine(directory, "pgproj.json");
            Assert.AreEqual(0, (await RunAsync("init", "--source-connection", connectionString, "--project", project)).ExitCode);
            var appFile = Path.Combine(directory, "sql", "Schema", "app", "app.sql");
            var auditFile = Path.Combine(directory, "sql", "Schema", "audit", "audit.sql");

            // A comment on a schema that has none.
            File.AppendAllText(auditFile, "COMMENT ON SCHEMA \"audit\" IS 'Who did what.';\n");
            var diffed = await RunAsync("diff", "--project", project, "--target-connection", connectionString);
            Assert.AreEqual(0, diffed.ExitCode, diffed.Output);
            StringAssert.Contains(diffed.Output, "Added: 1");
            var set = await RunAsync("publish", "--project", project, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, set.ExitCode, set.Output);
            Assert.AreEqual("Who did what.", await CommentOfAsync(connectionString, "audit"));
            await AssertDiffIsEmptyAsync(project, connectionString);

            // A changed one, with a quote and tags.
            File.WriteAllText(appFile, File.ReadAllText(appFile).Replace("The application''s tables.", "It''s the application.", StringComparison.Ordinal));
            var changed = await RunAsync("publish", "--project", project, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            StringAssert.StartsWith(await CommentOfAsync(connectionString, "app"), "It's the application.");
            await AssertDiffIsEmptyAsync(project, connectionString);

            // A stated NULL clears it.
            File.AppendAllText(auditFile, "COMMENT ON SCHEMA \"audit\" IS NULL;\n");
            var cleared = await RunAsync("publish", "--project", project, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, cleared.ExitCode, cleared.Output);
            Assert.AreEqual("<none>", await CommentOfAsync(connectionString, "audit"));
            await AssertDiffIsEmptyAsync(project, connectionString);
        });
    }

    [TestMethod]
    public async Task ACommentTheProjectDoesNotStateIsLeftAlone()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = CreateProjectDirectory("g160");
            try
            {
                WriteSchema(project, "CREATE SCHEMA app; CREATE TABLE app.customers (id bigint PRIMARY KEY); CREATE SCHEMA audit; CREATE TABLE audit.entry (id bigint PRIMARY KEY);");

                var diffed = await RunAsync("diff", "--project", ProjectFile(project), "--target-connection", connectionString);
                Assert.AreEqual(0, diffed.ExitCode, diffed.Output);
                StringAssert.Contains(diffed.Output, "Modified: 0");
                var published = await RunAsync("publish", "--project", ProjectFile(project), "--target-connection", connectionString, "--execute");
                Assert.AreEqual(0, published.ExitCode, published.Output);
                StringAssert.StartsWith(await CommentOfAsync(connectionString, "app"), "The application's tables.");
                Assert.AreEqual("standard public schema", await CommentOfAsync(connectionString, "public"), "public's comment was never touched.");
            }
            finally
            {
                TryDeleteDirectory(project);
            }
        });
    }

    [TestMethod]
    public async Task ANewSchemaIsCreatedWithItsComment()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = CreateProjectDirectory("g160new");
            try
            {
                WriteSchema(project, "CREATE TABLE app.customers (id bigint PRIMARY KEY); CREATE TABLE audit.entry (id bigint PRIMARY KEY); " +
                    "COMMENT ON SCHEMA reports IS 'Nothing here yet.'; COMMENT ON SCHEMA sales IS 'Sales data.'; CREATE TABLE sales.orders (id int PRIMARY KEY);");

                var published = await RunAsync("publish", "--project", ProjectFile(project), "--target-connection", connectionString, "--execute");

                Assert.AreEqual(0, published.ExitCode, published.Output);
                Assert.AreEqual("Nothing here yet.", await CommentOfAsync(connectionString, "reports"));
                Assert.AreEqual("Sales data.", await CommentOfAsync(connectionString, "sales"));
                await AssertDiffIsEmptyAsync(ProjectFile(project), connectionString);
            }
            finally
            {
                TryDeleteDirectory(project);
            }
        });
    }

    [TestMethod]
    public async Task DocumentShowsTheSchemasDescriptionAndTagsFromTheDatabaseAndTheProject()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var live = await RunAsync("document", "--source-connection", connectionString, "--output", Path.Combine(directory, "live"), "--format", "json,markdown,html");
            var project = Path.Combine(directory, "pgproj.json");
            Assert.AreEqual(0, (await RunAsync("init", "--source-connection", connectionString, "--project", project)).ExitCode);
            var fromProject = await RunAsync("document", "--project", project, "--output", Path.Combine(directory, "project"), "--format", "json");

            Assert.AreEqual(0, live.ExitCode, live.Output);
            Assert.AreEqual(0, fromProject.ExitCode, fromProject.Output);
            foreach (var folder in new[] { "live", "project" })
            {
                using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, folder, "database-documentation.json")));
                var app = json.RootElement.GetProperty("schemas").EnumerateArray().Single(static schema => schema.GetProperty("name").GetString() == "app");
                Assert.AreEqual("The application's tables.", app.GetProperty("description").GetString(), folder);
                Assert.AreEqual("Platform", app.GetProperty("owner").GetString(), folder);
                Assert.AreEqual("Application", app.GetProperty("businessName").GetString(), folder);
                var audit = json.RootElement.GetProperty("schemas").EnumerateArray().Single(static schema => schema.GetProperty("name").GetString() == "audit");
                Assert.IsFalse(audit.TryGetProperty("description", out _), folder);
            }

            var markdown = File.ReadAllText(Path.Combine(directory, "live", "database-documentation.md"));
            StringAssert.Contains(markdown, "## Schema app");
            StringAssert.Contains(markdown, "The application's tables.");
            StringAssert.Contains(File.ReadAllText(Path.Combine(directory, "live", "database-documentation.html")), "The application&#39;s tables.");
        });
    }

    private static async Task WithDatabaseAsync(Func<string, string, Task> test)
    {
        var databaseName = await CreateDatabaseAsync("g160");
        var directory = Path.Combine(Path.GetTempPath(), "pgproj-g160-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, Sql);
            await test(connectionString, directory);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(directory);
        }
    }
}
