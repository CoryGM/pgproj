using System.Text.Json;
using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-163 against a real server, on every version: a trigger's comment is read from the database, scaffolded by <c>init</c>, compared with the project's only when the project states one, set, changed, and cleared by
/// <c>publish</c> without dropping the trigger, put back when a trigger is dropped and created again, and shown by <c>document</c> with the trigger.
/// </summary>
[TestClass]
public sealed class TriggerCommentEndToEndTests
{
    private const string Setup = """
        CREATE SCHEMA app;
        CREATE TABLE app.orders (id bigint PRIMARY KEY, total numeric(10,2));
        CREATE TABLE app.audit_log (note text);
        CREATE FUNCTION app.audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN INSERT INTO app.audit_log VALUES (TG_OP); RETURN NEW; END $$;
        CREATE TRIGGER audit_insert AFTER INSERT ON app.orders FOR EACH ROW EXECUTE FUNCTION app.audit();
        CREATE TRIGGER audit_update AFTER UPDATE ON app.orders FOR EACH ROW EXECUTE FUNCTION app.audit();
        COMMENT ON TRIGGER audit_insert ON app.orders IS $$Writes the audit row.

        @owner: Platform$$;
        """;

    private static Task<string> CommentOfAsync(string connectionString, string trigger)
        => ScalarTextAsync(connectionString, $"select coalesce(obj_description(oid, 'pg_trigger'), '<none>') from pg_trigger where tgname = '{trigger}' and not tgisinternal");

    private static Task<string> TriggerOidAsync(string connectionString, string trigger)
        => ScalarTextAsync(connectionString, $"select oid from pg_trigger where tgname = '{trigger}' and not tgisinternal");

    [TestMethod]
    public async Task InitScaffoldsTheCommentAndTheProjectConvergesWithTheDatabase()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = Path.Combine(directory, "pgproj.json");
            var run = await RunAsync("init", "--source-connection", connectionString, "--project", project);

            Assert.AreEqual(0, run.ExitCode, run.Output);
            var orders = File.ReadAllText(Path.Combine(directory, "sql", "Schema", "app", "Table", "orders.sql"));
            StringAssert.Contains(orders, "COMMENT ON TRIGGER \"audit_insert\" ON app.orders IS ");
            StringAssert.Contains(orders, "Writes the audit row.");
            Assert.IsFalse(orders.Contains("COMMENT ON TRIGGER \"audit_update\"", StringComparison.Ordinal), "A trigger with no comment gets none.");
            await AssertDiffIsEmptyAsync(project, connectionString);
        });
    }

    [TestMethod]
    public async Task APublishSetsChangesAndClearsAStatedCommentWithoutDroppingTheTrigger()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = Path.Combine(directory, "pgproj.json");
            Assert.AreEqual(0, (await RunAsync("init", "--source-connection", connectionString, "--project", project)).ExitCode);
            var ordersFile = Path.Combine(directory, "sql", "Schema", "app", "Table", "orders.sql");
            var oidBefore = await TriggerOidAsync(connectionString, "audit_insert");

            // A comment on a trigger that has none.
            File.AppendAllText(ordersFile, "COMMENT ON TRIGGER audit_update ON app.orders IS 'Audits changes.';\n");
            var set = await RunAsync("publish", "--project", project, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, set.ExitCode, set.Output);
            Assert.AreEqual("Audits changes.", await CommentOfAsync(connectionString, "audit_update"));
            await AssertDiffIsEmptyAsync(project, connectionString);

            // A changed one: only the comment, so the trigger is not dropped (its oid is the same).
            File.WriteAllText(ordersFile, File.ReadAllText(ordersFile).Replace("Writes the audit row.", "It''s the insert audit.", StringComparison.Ordinal));
            var changed = await RunAsync("publish", "--project", project, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            StringAssert.StartsWith(await CommentOfAsync(connectionString, "audit_insert"), "It's the insert audit.");
            Assert.AreEqual(oidBefore, await TriggerOidAsync(connectionString, "audit_insert"), "A comment change does not drop and create the trigger.");
            await AssertDiffIsEmptyAsync(project, connectionString);

            // A stated NULL clears it.
            File.AppendAllText(ordersFile, "COMMENT ON TRIGGER audit_update ON app.orders IS NULL;\n");
            var cleared = await RunAsync("publish", "--project", project, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, cleared.ExitCode, cleared.Output);
            Assert.AreEqual("<none>", await CommentOfAsync(connectionString, "audit_update"));
            await AssertDiffIsEmptyAsync(project, connectionString);
        });
    }

    [TestMethod]
    public async Task ACommentTheProjectDoesNotStateIsLeftAloneEvenWhenTheTriggerIsRecreated()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = CreateProjectDirectory("g163");
            try
            {
                // The project says nothing about comments, and changes audit_insert so that it is dropped and created again.
                WriteSchema(project, Setup[..Setup.IndexOf("COMMENT ON TRIGGER", StringComparison.Ordinal)].Replace("AFTER INSERT ON", "AFTER INSERT OR DELETE ON", StringComparison.Ordinal));

                var published = await RunAsync("publish", "--project", ProjectFile(project), "--target-connection", connectionString, "--execute");

                Assert.AreEqual(0, published.ExitCode, published.Output);
                StringAssert.StartsWith(await CommentOfAsync(connectionString, "audit_insert"), "Writes the audit row.");
                StringAssert.Contains(await ScalarTextAsync(connectionString, "select pg_get_triggerdef(oid) from pg_trigger where tgname = 'audit_insert'"), "DELETE");
            }
            finally
            {
                TryDeleteDirectory(project);
            }
        });
    }

    [TestMethod]
    public async Task ARecreatedTriggerTakesTheProjectsNewComment()
    {
        await WithDatabaseAsync(async (connectionString, directory) =>
        {
            var project = CreateProjectDirectory("g163b");
            try
            {
                WriteSchema(project, Setup[..Setup.IndexOf("COMMENT ON TRIGGER", StringComparison.Ordinal)].Replace("AFTER INSERT ON", "AFTER INSERT OR DELETE ON", StringComparison.Ordinal) + "\nCOMMENT ON TRIGGER audit_insert ON app.orders IS 'Inserts and deletes.';");

                var published = await RunAsync("publish", "--project", ProjectFile(project), "--target-connection", connectionString, "--execute");

                Assert.AreEqual(0, published.ExitCode, published.Output);
                Assert.AreEqual("Inserts and deletes.", await CommentOfAsync(connectionString, "audit_insert"));
            }
            finally
            {
                TryDeleteDirectory(project);
            }
        });
    }

    [TestMethod]
    public async Task DocumentListsTheTriggersWithTheirDescriptionsFromTheDatabaseAndTheProject()
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
                var triggers = orders.GetProperty("triggers").EnumerateArray().ToDictionary(static trigger => trigger.GetProperty("name").GetString()!, static trigger => trigger.Clone());
                CollectionAssert.AreEquivalent(new[] { "audit_insert", "audit_update" }, triggers.Keys.ToArray(), folder);
                var insert = triggers["audit_insert"];
                Assert.AreEqual("Writes the audit row.", insert.GetProperty("description").GetString(), folder);
                Assert.AreEqual("Platform", insert.GetProperty("owner").GetString(), folder);
                Assert.AreEqual("AFTER", insert.GetProperty("timing").GetString(), folder);
                CollectionAssert.AreEqual(new[] { "INSERT" }, insert.GetProperty("events").EnumerateArray().Select(static e => e.GetString()).ToArray(), folder);
                Assert.AreEqual("row", insert.GetProperty("level").GetString(), folder);
                StringAssert.Contains(insert.GetProperty("function").GetString(), "audit", folder);
                Assert.IsFalse(triggers["audit_update"].TryGetProperty("description", out _), folder);
                Assert.AreEqual(2, json.RootElement.GetProperty("counts").GetProperty("triggers").GetInt32(), folder);
            }

            var markdown = File.ReadAllText(Path.Combine(directory, "live", "database-documentation.md"));
            StringAssert.Contains(markdown, "**Triggers**");
            StringAssert.Contains(markdown, "Writes the audit row.");
        });
    }

    private static async Task WithDatabaseAsync(Func<string, string, Task> test)
    {
        var databaseName = await CreateDatabaseAsync("g163");
        var directory = Path.Combine(Path.GetTempPath(), "pgproj-g163-" + Guid.NewGuid().ToString("N"));
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
