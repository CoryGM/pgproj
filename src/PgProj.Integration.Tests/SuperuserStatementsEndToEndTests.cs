using Npgsql;
using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-265 against a real server (signed off by the user on 2026-10-13). PostgreSQL lets only a superuser create an event trigger, a <c>LEAKPROOF</c> routine, or a routine in an
/// untrusted language; a role that is not one failed partway through the script. <c>publish --execute</c> now runs such statements only with
/// <c>--allow-superuser-statements</c> (even for a superuser), refuses a role that is not one before anything runs, and a dry run warns and heads the script with them.
/// </summary>
[TestClass]
public sealed class SuperuserStatementsEndToEndTests
{
    private const string Schema = """
        CREATE TABLE public.t (a integer);
        CREATE FUNCTION public.on_ddl() RETURNS event_trigger LANGUAGE plpgsql AS $$ BEGIN END $$;
        CREATE EVENT TRIGGER g265_ddl ON ddl_command_end EXECUTE FUNCTION public.on_ddl();
        CREATE FUNCTION public.lp(a integer) RETURNS integer LANGUAGE sql IMMUTABLE LEAKPROOF AS $$ SELECT a $$;
        """;

    [TestMethod]
    public async Task SuperuserStatementsNeedTheOptionAndASuperuser()
    {
        var databaseName = await CreateDatabaseAsync("g265");
        var projectDirectory = CreateProjectDirectory("g265", ", \"excludeGrants\": true");
        var role = "g265_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Schema);

            // What the gate stands for: PostgreSQL refuses each of these to a role that is not a superuser, even one that owns the database.
            await ExecuteAsync(connectionString, $"CREATE ROLE {role} LOGIN PASSWORD 'r'; ALTER DATABASE {databaseName} OWNER TO {role}; GRANT ALL ON SCHEMA public TO {role};");
            var roleConnection = new NpgsqlConnectionStringBuilder(connectionString) { Username = role, Password = "r" }.ConnectionString;
            await AssertRefusedAsync(roleConnection, "CREATE FUNCTION public.probe_et() RETURNS event_trigger LANGUAGE plpgsql AS $$ BEGIN END $$; CREATE EVENT TRIGGER probe ON ddl_command_end EXECUTE FUNCTION public.probe_et();");
            await AssertRefusedAsync(roleConnection, "CREATE FUNCTION public.probe_lp(a integer) RETURNS integer LANGUAGE sql LEAKPROOF AS $$ SELECT a $$;");
            await AssertRefusedAsync(roleConnection, "CREATE FUNCTION public.probe_in(integer) RETURNS integer LANGUAGE internal STRICT AS $$int4abs$$;");

            // Without the option, --execute refuses even for a superuser, and runs nothing.
            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(1, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "the script has 2 statement(s) that only a superuser can run, so it was not run:");
            StringAssert.Contains(refused.Output, "[EventTrigger] event trigger:g265_ddl: an event trigger is created, changed, or dropped only by a superuser");
            StringAssert.Contains(refused.Output, "[Function] public.lp(int4): only a superuser can make a routine LEAKPROOF");
            StringAssert.Contains(refused.Output, "pass --allow-superuser-statements");
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_class WHERE relname = 't' AND relnamespace = 'public'::regnamespace"), "Nothing ran.");

            // A dry run warns and heads the script with them.
            var dryRun = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.AreEqual(0, dryRun.ExitCode, dryRun.Output);
            StringAssert.Contains(dryRun.Output, "-- pgproj: this script has statements that only a superuser can run");
            StringAssert.Contains(dryRun.Output, "Warning: the script has statements that only a superuser can run; pgproj publish --execute runs them only with --allow-superuser-statements:");

            // With the option, a role that is not a superuser is refused before anything runs.
            var notSuper = await RunAsync("publish", "--project", projectFile, "--target-connection", roleConnection, "--execute", "--allow-superuser-statements");
            Assert.AreEqual(1, notSuper.ExitCode, notSuper.Output);
            StringAssert.Contains(notSuper.Output, "the connected role is not a superuser");
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_class WHERE relname = 't' AND relnamespace = 'public'::regnamespace"), "Nothing ran.");

            // With the option, a superuser publishes it, and it converges.
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-superuser-statements");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("1", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_event_trigger WHERE evtname = 'g265_ddl'"));
            Assert.AreEqual("true", await ScalarTextAsync(connectionString, "SELECT proleakproof::text FROM pg_proc WHERE oid = 'public.lp(integer)'::regprocedure"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // Nothing left that needs a superuser: the option is not needed, and a role that is not one may publish.
            var nothing = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, nothing.ExitCode, nothing.Output);
        }
        finally
        {
            await ExecuteAsync(ConnectionStringFor(databaseName), "DROP EVENT TRIGGER IF EXISTS g265_ddl;");
            await DropDatabaseAsync(databaseName);
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP ROLE IF EXISTS {role};");
            TryDeleteDirectory(projectDirectory);
        }
    }

    private static async Task AssertRefusedAsync(string connectionString, string sql)
    {
        var refused = false;
        try
        {
            await ExecuteAsync(connectionString, sql);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            refused = true;
        }

        Assert.IsTrue(refused, $"A role that is not a superuser ran: {sql}");
    }
}
