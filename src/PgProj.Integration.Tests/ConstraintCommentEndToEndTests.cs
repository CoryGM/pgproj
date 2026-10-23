using System.Text.Json;
using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-161 against a real server, on every version: a constraint's comment is read from the database, scaffolded by <c>init</c>, compared with the project's only when the project states one, set, changed, and cleared
/// by <c>publish</c> without dropping the constraint, put back when a constraint is dropped and added again, and shown by <c>document</c>.
/// </summary>
[TestClass]
public sealed class ConstraintCommentEndToEndTests
{
    private const string Tables = """
        CREATE SCHEMA app;
        CREATE TABLE app.customers (id bigint PRIMARY KEY, email text NOT NULL, CONSTRAINT customers_email_key UNIQUE (email));
        CREATE TABLE app.orders (
            id bigint PRIMARY KEY,
            customer_id bigint NOT NULL,
            total numeric(10,2),
            CONSTRAINT orders_customer_fk FOREIGN KEY (customer_id) REFERENCES app.customers (id),
            CONSTRAINT orders_total_ck CHECK (total >= 0));
        """;

    private const string Comments = """
        COMMENT ON CONSTRAINT customers_pkey ON app.customers IS $$The customer's key.

        @owner: Platform$$;
        COMMENT ON CONSTRAINT orders_total_ck ON app.orders IS 'Never negative.';
        """;

    private static Task<string> CommentOfAsync(string connectionString, string constraint)
        => ScalarTextAsync(connectionString, $"select coalesce(obj_description(oid, 'pg_constraint'), '<none>') from pg_constraint where conname = '{constraint}'");

    private static Task<string> ConstraintOidAsync(string connectionString, string constraint)
        => ScalarTextAsync(connectionString, $"select oid from pg_constraint where conname = '{constraint}'");

    [TestMethod]
    public async Task InitScaffoldsTheCommentsAndTheProjectConvergesWithTheDatabase()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = Path.Combine(directory, "pgproj.json");
            var run = await RunAsync("init", "--source-connection", connectionString, "--project", project);

            Assert.AreEqual(0, run.ExitCode, run.Output);
            var customers = File.ReadAllText(Path.Combine(directory, "sql", "Schema", "app", "Table", "customers.sql"));
            StringAssert.Contains(customers, "COMMENT ON CONSTRAINT \"customers_pkey\" ON app.customers IS ");
            StringAssert.Contains(customers, "The customer''s key.");
            Assert.IsFalse(customers.Contains("COMMENT ON CONSTRAINT \"customers_email_key\"", StringComparison.Ordinal), "A constraint with no comment gets none.");
            StringAssert.Contains(File.ReadAllText(Path.Combine(directory, "sql", "Schema", "app", "Table", "orders.sql")), "COMMENT ON CONSTRAINT \"orders_total_ck\" ON app.orders IS 'Never negative.';");
            await AssertDiffIsEmptyAsync(project, connectionString);
        });
    }

    [TestMethod]
    public async Task APublishSetsChangesAndClearsAStatedCommentWithoutDroppingTheConstraint()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = Path.Combine(directory, "pgproj.json");
            Assert.AreEqual(0, (await RunAsync("init", "--source-connection", connectionString, "--project", project)).ExitCode);
            var customersFile = Path.Combine(directory, "sql", "Schema", "app", "Table", "customers.sql");
            var ordersFile = Path.Combine(directory, "sql", "Schema", "app", "Table", "orders.sql");
            var oidBefore = await ConstraintOidAsync(connectionString, "orders_total_ck");

            // A comment on a constraint that has none.
            File.AppendAllText(customersFile, "COMMENT ON CONSTRAINT customers_email_key ON app.customers IS 'One account per address.';\n");
            var set = await RunAsync("publish", "--project", project, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, set.ExitCode, set.Output);
            Assert.AreEqual("One account per address.", await CommentOfAsync(connectionString, "customers_email_key"));
            await AssertDiffIsEmptyAsync(project, connectionString);

            // A changed one: only the comment, so the constraint is not dropped (its oid is the same).
            File.WriteAllText(ordersFile, File.ReadAllText(ordersFile).Replace("Never negative.", "It''s never negative.", StringComparison.Ordinal));
            var changed = await RunAsync("publish", "--project", project, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            Assert.AreEqual("It's never negative.", await CommentOfAsync(connectionString, "orders_total_ck"));
            Assert.AreEqual(oidBefore, await ConstraintOidAsync(connectionString, "orders_total_ck"), "A comment change does not drop and add the constraint.");
            await AssertDiffIsEmptyAsync(project, connectionString);

            // A stated NULL clears it.
            File.AppendAllText(ordersFile, "COMMENT ON CONSTRAINT orders_total_ck ON app.orders IS NULL;\n");
            var cleared = await RunAsync("publish", "--project", project, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, cleared.ExitCode, cleared.Output);
            Assert.AreEqual("<none>", await CommentOfAsync(connectionString, "orders_total_ck"));
            await AssertDiffIsEmptyAsync(project, connectionString);
        });
    }

    [TestMethod]
    public async Task ACommentTheProjectDoesNotStateIsLeftAloneEvenWhenTheConstraintIsRecreated()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = CreateProjectDirectory("g161");
            try
            {
                // The project says nothing about comments, and changes the CHECK, which is dropped and added again.
                WriteSchema(project, Tables.Replace("total >= 0", "total >= 1", StringComparison.Ordinal));

                var diffed = await RunAsync("diff", "--project", ProjectFile(project), "--target-connection", connectionString);
                Assert.AreEqual(0, diffed.ExitCode, diffed.Output);
                var published = await RunAsync("publish", "--project", ProjectFile(project), "--target-connection", connectionString, "--execute");
                Assert.AreEqual(0, published.ExitCode, published.Output);
                Assert.AreEqual("Never negative.", await CommentOfAsync(connectionString, "orders_total_ck"), "The comment the database had comes back with the constraint.");
                StringAssert.Contains(await ScalarTextAsync(connectionString, "select pg_get_constraintdef(oid) from pg_constraint where conname = 'orders_total_ck'"), "(1)");
                StringAssert.StartsWith(await CommentOfAsync(connectionString, "customers_pkey"), "The customer's key.");
            }
            finally
            {
                TryDeleteDirectory(project);
            }
        });
    }

    [TestMethod]
    public async Task ARecreatedConstraintTakesTheProjectsNewComment()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = CreateProjectDirectory("g161b");
            try
            {
                WriteSchema(project, Tables.Replace("total >= 0", "total >= 1", StringComparison.Ordinal) + "\nCOMMENT ON CONSTRAINT orders_total_ck ON app.orders IS 'At least one.';");

                var published = await RunAsync("publish", "--project", ProjectFile(project), "--target-connection", connectionString, "--execute");

                Assert.AreEqual(0, published.ExitCode, published.Output);
                Assert.AreEqual("At least one.", await CommentOfAsync(connectionString, "orders_total_ck"));
            }
            finally
            {
                TryDeleteDirectory(project);
            }
        });
    }

    [TestMethod]
    public async Task ANewConstraintIsAddedWithItsComment()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = CreateProjectDirectory("g161c");
            try
            {
                WriteSchema(project, Tables + "\nALTER TABLE app.orders ADD CONSTRAINT orders_id_pos CHECK (id > 0);\nCOMMENT ON CONSTRAINT orders_id_pos ON app.orders IS $$It's positive.$$;");

                var published = await RunAsync("publish", "--project", ProjectFile(project), "--target-connection", connectionString, "--execute");

                Assert.AreEqual(0, published.ExitCode, published.Output);
                Assert.AreEqual("It's positive.", await CommentOfAsync(connectionString, "orders_id_pos"));
                Assert.AreEqual("Never negative.", await CommentOfAsync(connectionString, "orders_total_ck"));
            }
            finally
            {
                TryDeleteDirectory(project);
            }
        });
    }

    [TestMethod]
    public async Task DocumentShowsAConstraintsDescriptionAndTagsFromTheDatabaseAndTheProject()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var live = await RunAsync("document", "--source-connection", connectionString, "--output", Path.Combine(directory, "live"), "--format", "json,markdown");
            var project = Path.Combine(directory, "pgproj.json");
            Assert.AreEqual(0, (await RunAsync("init", "--source-connection", connectionString, "--project", project)).ExitCode);
            var fromProject = await RunAsync("document", "--project", project, "--output", Path.Combine(directory, "project"), "--format", "json");

            Assert.AreEqual(0, live.ExitCode, live.Output);
            Assert.AreEqual(0, fromProject.ExitCode, fromProject.Output);
            foreach (var folder in new[] { "live", "project" })
            {
                using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, folder, "database-documentation.json")));
                var customers = json.RootElement.GetProperty("schemas")[0].GetProperty("tables").EnumerateArray().Single(static table => table.GetProperty("name").GetString() == "customers");
                var key = customers.GetProperty("constraints").EnumerateArray().Single(static constraint => constraint.GetProperty("name").GetString() == "customers_pkey");
                Assert.AreEqual("The customer's key.", key.GetProperty("description").GetString(), folder);
                Assert.AreEqual("Platform", key.GetProperty("owner").GetString(), folder);
                var email = customers.GetProperty("constraints").EnumerateArray().Single(static constraint => constraint.GetProperty("name").GetString() == "customers_email_key");
                Assert.IsFalse(email.TryGetProperty("description", out _), folder);
            }

            StringAssert.Contains(File.ReadAllText(Path.Combine(directory, "live", "database-documentation.md")), "The customer's key.");
        });
    }

    private static async Task WithDatabaseAsync(Func<string, string, Task> test)
    {
        var databaseName = await CreateDatabaseAsync("g161");
        var directory = Path.Combine(Path.GetTempPath(), "pgproj-g161-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, Tables + Environment.NewLine + Comments);
            await test(connectionString, directory);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(directory);
        }
    }
}
