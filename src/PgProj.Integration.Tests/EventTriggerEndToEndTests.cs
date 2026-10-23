using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-127 against a real server, on every version: event triggers deploy as written after the function they run, read back from
/// <c>pg_event_trigger</c>, converge <c>0/0/0</c>, are dropped and created again when they change, round-trip through <c>init</c> into a file of their own, and
/// are not dropped by accident: removing one needs <c>--allow-destructive</c>, one that belongs to an extension is left alone, and a project that
/// lists <c>schemas</c> only manages the ones whose function is in those schemas.
/// </summary>
[TestClass]
public sealed class EventTriggerEndToEndTests
{
    private const string Schema = """
        CREATE FUNCTION public.log_ddl() RETURNS event_trigger LANGUAGE plpgsql AS $$ BEGIN NULL; END $$;
        CREATE EVENT TRIGGER audit_ddl ON ddl_command_end WHEN TAG IN ('CREATE TABLE', 'ALTER TABLE') EXECUTE FUNCTION public.log_ddl();
        CREATE EVENT TRIGGER audit_drop ON sql_drop EXECUTE FUNCTION public.log_ddl();
        ALTER EVENT TRIGGER audit_drop DISABLE;
        """;

    [TestMethod]
    public async Task EventTriggersDeployConvergeChangeRoundTripThroughInitAndAreNotDroppedByAccident()
    {
        var databaseName = await CreateDatabaseAsync("g127");
        var scaffoldDatabaseName = await CreateDatabaseAsync("g127s");
        var projectDirectory = CreateProjectDirectory("g127");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Schema);

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            StringAssert.Contains(built.Output, "No diagnostics.");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-superuser-statements");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("audit_ddl:ddl_command_end:{ALTER TABLE,CREATE TABLE}:O|audit_drop:sql_drop:-:D", await EventTriggers(connectionString));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A second publish changes nothing.
            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-superuser-statements");
            Assert.AreEqual(0, again.ExitCode, again.Output);

            // Round trip through init: each event trigger is in a file of its own, and the scaffold deploys the same into a second database.
            var scaffoldDirectory = Path.Combine(projectDirectory, "scaffold");
            var scaffolded = await RunAsync("init", "--source-connection", connectionString, "--output", scaffoldDirectory, "--project", Path.Combine(scaffoldDirectory, "pgproj.json"));
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            Assert.IsTrue(File.Exists(Path.Combine(scaffoldDirectory, "EventTrigger", "audit_ddl.sql")));
            Assert.IsTrue(File.Exists(Path.Combine(scaffoldDirectory, "EventTrigger", "audit_drop.sql")));
            await AssertDiffIsEmptyAsync(Path.Combine(scaffoldDirectory, "pgproj.json"), connectionString);
            var scaffoldConnectionString = ConnectionStringFor(scaffoldDatabaseName);
            var deployedScaffold = await RunAsync("publish", "--project", Path.Combine(scaffoldDirectory, "pgproj.json"), "--target-connection", scaffoldConnectionString, "--execute", "--allow-superuser-statements");
            Assert.AreEqual(0, deployedScaffold.ExitCode, deployedScaffold.Output);
            Assert.AreEqual("audit_ddl:ddl_command_end:{ALTER TABLE,CREATE TABLE}:O|audit_drop:sql_drop:-:D", await EventTriggers(scaffoldConnectionString));
            await AssertDiffIsEmptyAsync(projectFile, scaffoldConnectionString);

            // A changed filter and a changed mode are each a drop and a create, and are not destructive.
            WriteSchema(projectDirectory, Edit(
                Edit(Schema, "('CREATE TABLE', 'ALTER TABLE')", "('CREATE TABLE', 'DROP TABLE')"),
                "ALTER EVENT TRIGGER audit_drop DISABLE;", "ALTER EVENT TRIGGER audit_drop ENABLE ALWAYS;"));
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-superuser-statements");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            Assert.AreEqual("audit_ddl:ddl_command_end:{CREATE TABLE,DROP TABLE}:O|audit_drop:sql_drop:-:A", await EventTriggers(connectionString));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // Removing one is destructive: it stops guarding every DDL command it ran on.
            WriteSchema(projectDirectory, Edit(
                Edit(Schema, "CREATE EVENT TRIGGER audit_drop ON sql_drop EXECUTE FUNCTION public.log_ddl();\nALTER EVENT TRIGGER audit_drop DISABLE;", string.Empty),
                "('CREATE TABLE', 'ALTER TABLE')", "('CREATE TABLE', 'DROP TABLE')"));
            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-superuser-statements");
            Assert.AreEqual(1, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "event trigger dropped; it no longer runs on sql_drop");
            Assert.AreEqual(2L, long.Parse(await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_event_trigger;")), "Nothing was dropped.");

            var allowed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive", "--allow-superuser-statements");
            Assert.AreEqual(0, allowed.ExitCode, allowed.Output);
            Assert.AreEqual("audit_ddl:ddl_command_end:{CREATE TABLE,DROP TABLE}:O", await EventTriggers(connectionString));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await DropDatabaseAsync(scaffoldDatabaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task AnEventTriggerThatAnExtensionOwnsIsLeftAlone()
    {
        var databaseName = await CreateDatabaseAsync("g127x");
        var projectDirectory = CreateProjectDirectory("g127x");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, """
                CREATE EXTENSION btree_gist;
                CREATE FUNCTION public.ext_fn() RETURNS event_trigger LANGUAGE plpgsql AS $$ BEGIN NULL; END $$;
                CREATE EVENT TRIGGER ext_owned ON ddl_command_end EXECUTE FUNCTION public.ext_fn();
                ALTER EXTENSION btree_gist ADD FUNCTION public.ext_fn();
                ALTER EXTENSION btree_gist ADD EVENT TRIGGER ext_owned;
                """);
            WriteSchema(projectDirectory, "CREATE EXTENSION btree_gist;\n");

            // pgaudit and its kind install event triggers this way; the project neither declares nor removes them.
            await AssertDiffIsEmptyAsync(ProjectFile(projectDirectory), connectionString);
            var published = await RunAsync("publish", "--project", ProjectFile(projectDirectory), "--target-connection", connectionString, "--execute", "--allow-superuser-statements");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("1", await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_event_trigger WHERE evtname = 'ext_owned';"));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task AProjectThatListsSchemasOnlyManagesTheEventTriggersWhoseFunctionIsInThem()
    {
        var databaseName = await CreateDatabaseAsync("g127s");
        var projectDirectory = CreateProjectDirectory("g127sc", ", \"schemas\": [\"app\"]");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, """
                CREATE FUNCTION public.other_fn() RETURNS event_trigger LANGUAGE plpgsql AS $$ BEGIN NULL; END $$;
                CREATE EVENT TRIGGER other_project ON ddl_command_end EXECUTE FUNCTION public.other_fn();
                """);
            WriteSchema(projectDirectory, """
                CREATE SCHEMA app;
                CREATE FUNCTION app.log_ddl() RETURNS event_trigger LANGUAGE plpgsql AS $$ BEGIN NULL; END $$;
                CREATE EVENT TRIGGER app_audit ON ddl_command_end EXECUTE FUNCTION app.log_ddl();
                """);

            var published = await RunAsync("publish", "--project", ProjectFile(projectDirectory), "--target-connection", connectionString, "--execute", "--allow-superuser-statements");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("app_audit|other_project", await ScalarTextAsync(connectionString, "SELECT string_agg(evtname, '|' ORDER BY evtname) FROM pg_event_trigger;"));
            await AssertDiffIsEmptyAsync(ProjectFile(projectDirectory), connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    private static Task<string> EventTriggers(string connectionString)
        => ScalarTextAsync(
            connectionString,
            "SELECT string_agg(evtname || ':' || evtevent || ':' || coalesce((SELECT '{' || string_agg(t, ',' ORDER BY t) || '}' FROM unnest(evttags) AS t), '-') || ':' || evtenabled::text, '|' ORDER BY evtname) FROM pg_event_trigger;");
}
