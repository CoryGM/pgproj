using Npgsql;
using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-261 against a real server (option (a), decided by the user on 2026-10-13). <c>ALTER TABLE t { ENABLE | DISABLE } TRIGGER ALL</c> was reported as not modeled and ignored. It is now deployed
/// as written (at the end of the script, behind <c>--allow-superuser-statements</c>, since PostgreSQL lets only a superuser touch the internal foreign-key triggers), read back, compared only
/// when the project states it, scaffolded by <c>init</c>, and the declared triggers take the state as with <c>TRIGGER USER</c>.
/// </summary>
[TestClass]
public sealed class InternalTriggerStateEndToEndTests
{
    private const string Objects = """
        CREATE TABLE public.p (id integer PRIMARY KEY);
        CREATE TABLE public.c (id integer, p_id integer REFERENCES public.p (id));
        CREATE FUNCTION public.f() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
        CREATE TRIGGER trg BEFORE INSERT ON public.c FOR EACH ROW EXECUTE FUNCTION public.f();
        CREATE TRIGGER trg2 BEFORE UPDATE ON public.c FOR EACH ROW EXECUTE FUNCTION public.f();
        """;

    // The states of c's internal triggers, p's internal triggers (which TRIGGER ALL on c leaves alone), and c's declared triggers.
    private const string States = """
        SELECT (SELECT string_agg(DISTINCT tgenabled::text, ',') FROM pg_trigger WHERE tgrelid = 'public.c'::regclass AND tgisinternal)
            || '|' || (SELECT string_agg(DISTINCT tgenabled::text, ',') FROM pg_trigger WHERE tgrelid = 'public.p'::regclass AND tgisinternal)
            || '|' || (SELECT string_agg(tgname || ':' || tgenabled::text, ',' ORDER BY tgname) FROM pg_trigger WHERE tgrelid = 'public.c'::regclass AND NOT tgisinternal)
        """;

    [TestMethod]
    public async Task TriggerAllIsDeployedBehindTheSuperuserOptionReadBackComparedAndScaffolded()
    {
        var databaseName = await CreateDatabaseAsync("g261");
        var projectDirectory = CreateProjectDirectory("g261", ", \"excludeGrants\": true");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g261-init-" + Guid.NewGuid().ToString("N"));
        var secondDatabase = await CreateDatabaseAsync("g261b");
        var role = "g261_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);

            // What the gate stands for: a role that is not a superuser may not touch the internal triggers, even of a table it owns.
            await ExecuteAsync(connectionString, $"CREATE ROLE {role} LOGIN PASSWORD 'r'; ALTER DATABASE {databaseName} OWNER TO {role}; GRANT ALL ON SCHEMA public TO {role};");
            var roleConnection = new NpgsqlConnectionStringBuilder(connectionString) { Username = role, Password = "r" }.ConnectionString;
            var refusedToRole = false;
            try
            {
                await ExecuteAsync(roleConnection, "CREATE TABLE public.rp (id integer PRIMARY KEY); CREATE TABLE public.rc (p integer REFERENCES public.rp (id)); ALTER TABLE public.rc DISABLE TRIGGER ALL;");
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                refusedToRole = true;
            }

            Assert.IsTrue(refusedToRole, "A role that is not a superuser ran ALTER TABLE ... DISABLE TRIGGER ALL.");

            // Project: the internal triggers of c disabled, and trg2 set on its own after ALL reached it.
            WriteSchema(projectDirectory, Objects + """
                ALTER TABLE public.c ENABLE ALWAYS TRIGGER trg2;
                ALTER TABLE public.c DISABLE TRIGGER ALL;
                """);
            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            Assert.IsFalse(built.Output.Contains("is not modeled", StringComparison.Ordinal), built.Output);

            // Without the option --execute refuses, and runs nothing.
            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(1, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "only a superuser can enable or disable a table's internal (foreign-key) triggers (TRIGGER ALL)");
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_class WHERE relname = 'c' AND relnamespace = 'public'::regnamespace"), "Nothing ran.");

            // The dry run puts the statement last.
            var dryRun = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.AreEqual(0, dryRun.ExitCode, dryRun.Output);
            Assert.IsTrue(dryRun.Output.LastIndexOf("DISABLE TRIGGER ALL;", StringComparison.Ordinal) > dryRun.Output.LastIndexOf("FOREIGN KEY", StringComparison.Ordinal), dryRun.Output);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-superuser-statements");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("D|O|trg:D,trg2:A", await ScalarTextAsync(connectionString, States));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A second publish has nothing to do, so it needs no option.
            var second = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, second.ExitCode, second.Output);

            // init writes the state, and the scaffold publishes into another database with the same ones.
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString, "--exclude-owners", "--exclude-grants");
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            Assert.IsFalse(scaffolded.Output.Contains("not all in one state", StringComparison.Ordinal), scaffolded.Output);
            var secondConnection = ConnectionStringFor(secondDatabase);
            var republished = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute", "--allow-superuser-statements");
            Assert.AreEqual(0, republished.ExitCode, "publishing the scaffold failed.\n" + republished.Output);
            Assert.AreEqual("D|O|trg:D,trg2:A", await ScalarTextAsync(secondConnection, States));
            await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);

            // A project that says nothing leaves the database's state alone.
            WriteSchema(projectDirectory, Objects);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A changed state is made in place: the internal triggers are enabled again, trg2 keeps its own.
            WriteSchema(projectDirectory, Objects + """
                ALTER TABLE public.c ENABLE ALWAYS TRIGGER trg2;
                ALTER TABLE public.c ENABLE TRIGGER ALL;
                """);
            var cOid = await ScalarTextAsync(connectionString, "SELECT 'public.c'::regclass::oid::text");
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-superuser-statements");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            Assert.AreEqual("O|O|trg:O,trg2:A", await ScalarTextAsync(connectionString, States));
            Assert.AreEqual(cOid, await ScalarTextAsync(connectionString, "SELECT 'public.c'::regclass::oid::text"), "The table is altered in place.");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // Internal triggers in mixed states cannot be stated: init writes the most common one and says so.
            await ExecuteAsync(connectionString, "DO $$ DECLARE n text; BEGIN SELECT tgname INTO n FROM pg_trigger WHERE tgrelid = 'public.c'::regclass AND tgisinternal LIMIT 1; EXECUTE format('ALTER TABLE public.c DISABLE TRIGGER %I', n); END $$;");
            var mixedDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g261-mixed-" + Guid.NewGuid().ToString("N"));
            try
            {
                var mixed = await RunAsync("init", "--project", Path.Combine(mixedDirectory, "pgproj.json"), "--source-connection", connectionString, "--exclude-owners", "--exclude-grants");
                Assert.AreEqual(0, mixed.ExitCode, mixed.Output);
                StringAssert.Contains(mixed.Output, "not all in one state ENABLE or DISABLE TRIGGER ALL can state");
                StringAssert.Contains(mixed.Output, "public.c");
            }
            finally
            {
                TryDeleteDirectory(mixedDirectory);
            }
        }
        finally
        {
            await DropDatabaseAsync(secondDatabase);
            await DropDatabaseAsync(databaseName);
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP ROLE IF EXISTS {role};");
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
