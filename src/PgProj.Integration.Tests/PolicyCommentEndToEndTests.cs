using System.Text.Json;
using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-164 against a real server, on every version: a policy's comment is read from the database, scaffolded by <c>init</c>, compared with the project's only when the project states one, set, changed, and cleared by
/// <c>publish</c> without dropping the policy, put back when a policy is dropped and created again, and shown by <c>document</c> with the policy.
/// </summary>
[TestClass]
public sealed class PolicyCommentEndToEndTests
{
    private const string Setup = """
        CREATE SCHEMA app;
        CREATE TABLE app.orders (id bigint PRIMARY KEY, owner_name text);
        ALTER TABLE app.orders ENABLE ROW LEVEL SECURITY;
        CREATE POLICY own_rows ON app.orders FOR SELECT TO PUBLIC USING (owner_name = current_user);
        CREATE POLICY insert_rows ON app.orders FOR INSERT TO PUBLIC WITH CHECK (owner_name = current_user);
        COMMENT ON POLICY own_rows ON app.orders IS $$Users see their own rows.

        @owner: Security$$;
        """;

    private static Task<string> CommentOfAsync(string connectionString, string policy)
        => ScalarTextAsync(connectionString, $"select coalesce(obj_description(oid, 'pg_policy'), '<none>') from pg_policy where polname = '{policy}'");

    private static Task<string> PolicyOidAsync(string connectionString, string policy)
        => ScalarTextAsync(connectionString, $"select oid from pg_policy where polname = '{policy}'");

    [TestMethod]
    public async Task InitScaffoldsTheCommentAndTheProjectConvergesWithTheDatabase()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = Path.Combine(directory, "pgproj.json");
            var run = await RunAsync("init", "--source-connection", connectionString, "--project", project);

            Assert.AreEqual(0, run.ExitCode, run.Output);
            var orders = File.ReadAllText(Path.Combine(directory, "sql", "Schema", "app", "Table", "orders.sql"));
            StringAssert.Contains(orders, "COMMENT ON POLICY \"own_rows\" ON app.orders IS ");
            StringAssert.Contains(orders, "Users see their own rows.");
            Assert.IsFalse(orders.Contains("COMMENT ON POLICY \"insert_rows\"", StringComparison.Ordinal), "A policy with no comment gets none.");
            await AssertDiffIsEmptyAsync(project, connectionString);
        });
    }

    [TestMethod]
    public async Task APublishSetsChangesAndClearsAStatedCommentWithoutDroppingThePolicy()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = Path.Combine(directory, "pgproj.json");
            Assert.AreEqual(0, (await RunAsync("init", "--source-connection", connectionString, "--project", project)).ExitCode);
            var ordersFile = Path.Combine(directory, "sql", "Schema", "app", "Table", "orders.sql");
            var oidBefore = await PolicyOidAsync(connectionString, "own_rows");

            // A comment on a policy that has none.
            File.AppendAllText(ordersFile, "COMMENT ON POLICY insert_rows ON app.orders IS 'Users write their own rows.';\n");
            var set = await RunAsync("publish", "--project", project, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, set.ExitCode, set.Output);
            Assert.AreEqual("Users write their own rows.", await CommentOfAsync(connectionString, "insert_rows"));
            await AssertDiffIsEmptyAsync(project, connectionString);

            // A changed one: only the comment, so the policy is not dropped (its oid is the same).
            File.WriteAllText(ordersFile, File.ReadAllText(ordersFile).Replace("Users see their own rows.", "It''s the read policy.", StringComparison.Ordinal));
            var changed = await RunAsync("publish", "--project", project, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            StringAssert.StartsWith(await CommentOfAsync(connectionString, "own_rows"), "It's the read policy.");
            Assert.AreEqual(oidBefore, await PolicyOidAsync(connectionString, "own_rows"), "A comment change does not drop and create the policy.");
            await AssertDiffIsEmptyAsync(project, connectionString);

            // A stated NULL clears it.
            File.AppendAllText(ordersFile, "COMMENT ON POLICY insert_rows ON app.orders IS NULL;\n");
            var cleared = await RunAsync("publish", "--project", project, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, cleared.ExitCode, cleared.Output);
            Assert.AreEqual("<none>", await CommentOfAsync(connectionString, "insert_rows"));
            await AssertDiffIsEmptyAsync(project, connectionString);
        });
    }

    [TestMethod]
    public async Task ACommentTheProjectDoesNotStateIsLeftAloneEvenWhenThePolicyIsRecreated()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = CreateProjectDirectory("g164");
            try
            {
                // The project says nothing about comments, and changes own_rows so that it is dropped and created again.
                WriteSchema(project, Setup[..Setup.IndexOf("COMMENT ON POLICY", StringComparison.Ordinal)].Replace("FOR SELECT TO PUBLIC USING (owner_name = current_user)", "FOR SELECT TO PUBLIC USING (owner_name = current_user OR owner_name IS NULL)", StringComparison.Ordinal));

                var published = await RunAsync("publish", "--project", ProjectFile(project), "--target-connection", connectionString, "--execute");

                Assert.AreEqual(0, published.ExitCode, published.Output);
                StringAssert.StartsWith(await CommentOfAsync(connectionString, "own_rows"), "Users see their own rows.");
                StringAssert.Contains(await ScalarTextAsync(connectionString, "select pg_get_expr(polqual, polrelid) from pg_policy where polname = 'own_rows'"), "IS NULL");
            }
            finally
            {
                TryDeleteDirectory(project);
            }
        });
    }

    [TestMethod]
    public async Task ARecreatedPolicyTakesTheProjectsNewComment()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = CreateProjectDirectory("g164b");
            try
            {
                WriteSchema(project, Setup[..Setup.IndexOf("COMMENT ON POLICY", StringComparison.Ordinal)].Replace("FOR SELECT TO PUBLIC USING (owner_name = current_user)", "FOR SELECT TO PUBLIC USING (owner_name = current_user OR owner_name IS NULL)", StringComparison.Ordinal) + "\nCOMMENT ON POLICY own_rows ON app.orders IS 'Own rows and unowned rows.';");

                var published = await RunAsync("publish", "--project", ProjectFile(project), "--target-connection", connectionString, "--execute");

                Assert.AreEqual(0, published.ExitCode, published.Output);
                Assert.AreEqual("Own rows and unowned rows.", await CommentOfAsync(connectionString, "own_rows"));
            }
            finally
            {
                TryDeleteDirectory(project);
            }
        });
    }

    [TestMethod]
    public async Task DocumentListsThePoliciesWithTheirDescriptionsFromTheDatabaseAndTheProject()
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
                var orders = json.RootElement.GetProperty("schemas")[0].GetProperty("tables").EnumerateArray().Single(static table => table.GetProperty("name").GetString() == "orders");
                Assert.IsTrue(orders.GetProperty("rowSecurity").GetBoolean(), folder);
                var policies = orders.GetProperty("policies").EnumerateArray().ToDictionary(static policy => policy.GetProperty("name").GetString()!, static policy => policy.Clone());
                CollectionAssert.AreEquivalent(new[] { "own_rows", "insert_rows" }, policies.Keys.ToArray(), folder);
                var own = policies["own_rows"];
                Assert.AreEqual("Users see their own rows.", own.GetProperty("description").GetString(), folder);
                Assert.AreEqual("Security", own.GetProperty("owner").GetString(), folder);
                Assert.AreEqual("SELECT", own.GetProperty("command").GetString(), folder);
                StringAssert.Contains(own.GetProperty("using").GetString()!.ToLowerInvariant(), "current_user", folder);
                StringAssert.Contains(policies["insert_rows"].GetProperty("withCheck").GetString()!.ToLowerInvariant(), "current_user", folder);
                Assert.IsFalse(policies["insert_rows"].TryGetProperty("description", out _), folder);
                Assert.AreEqual(2, json.RootElement.GetProperty("counts").GetProperty("policies").GetInt32(), folder);
            }

            var markdown = File.ReadAllText(Path.Combine(directory, "live", "database-documentation.md"));
            StringAssert.Contains(markdown, "**Row-level security policies**");
            StringAssert.Contains(markdown, "Users see their own rows.");
        });
    }

    private static async Task WithDatabaseAsync(Func<string, string, Task> test)
    {
        var databaseName = await CreateDatabaseAsync("g164");
        var directory = Path.Combine(Path.GetTempPath(), "pgproj-g164-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, Setup);
            await test(connectionString, directory);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(directory);
        }
    }
}
