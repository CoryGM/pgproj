using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-257 against a real server. A trigger or rule the source database had disabled (or set to fire on replicas) was created enabled, because <c>ALTER TABLE ... DISABLE TRIGGER</c> was reported as not
/// modeled. The firing mode is now deployed after the trigger or rule, read back, changed in place (same trigger), compared only when the project states one, and scaffolded by <c>init</c>.
/// </summary>
[TestClass]
public sealed class TriggerAndRuleStateEndToEndTests
{
    private const string Objects = """
        CREATE TABLE public.t (id integer);
        CREATE FUNCTION public.f() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
        CREATE TRIGGER trg BEFORE INSERT ON public.t FOR EACH ROW EXECUTE FUNCTION public.f();
        CREATE TRIGGER trg2 BEFORE UPDATE ON public.t FOR EACH ROW EXECUTE FUNCTION public.f();
        CREATE RULE r AS ON INSERT TO public.t DO ALSO NOTHING;
        """;

    /// <summary>G-259: <c>DISABLE TRIGGER USER</c> reaches every trigger the project declares on the table, in any file order, and a statement naming one trigger overrides it.</summary>
    [TestMethod]
    public async Task TriggerUserReachesEveryDeclaredTriggerAndANamedStatementOverridesIt()
    {
        var databaseName = await CreateDatabaseAsync("g259");
        var projectDirectory = CreateProjectDirectory("g259");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            const string states = "SELECT string_agg(tgname || ':' || tgenabled::text, ',' ORDER BY tgname) FROM pg_trigger WHERE tgrelid = 'public.t'::regclass AND NOT tgisinternal";

            // The statement comes before the triggers it covers.
            WriteSchema(projectDirectory, """
                ALTER TABLE public.t DISABLE TRIGGER USER;
                ALTER TABLE public.t ENABLE ALWAYS TRIGGER trg2;
                """ + Objects);
            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            Assert.IsFalse(built.Output.Contains("is not modeled", StringComparison.Ordinal), built.Output);
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("trg:D,trg2:A", await ScalarTextAsync(connectionString, states));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task ADisabledTriggerAndRuleKeepTheirStateAreChangedInPlaceAndAreScaffolded()
    {
        var databaseName = await CreateDatabaseAsync("g257");
        var projectDirectory = CreateProjectDirectory("g257");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g257-init-" + Guid.NewGuid().ToString("N"));
        var secondDatabase = await CreateDatabaseAsync("g257b");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            const string states = "SELECT (SELECT string_agg(tgname || ':' || tgenabled::text, ',' ORDER BY tgname) FROM pg_trigger WHERE tgrelid = 'public.t'::regclass AND NOT tgisinternal) || '|' || (SELECT string_agg(rulename || ':' || ev_enabled::text, ',') FROM pg_rewrite WHERE ev_class = 'public.t'::regclass)";

            WriteSchema(projectDirectory, Objects + """
                ALTER TABLE public.t DISABLE TRIGGER trg;
                ALTER TABLE public.t ENABLE REPLICA TRIGGER trg2;
                ALTER TABLE public.t DISABLE RULE r;
                """);

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            Assert.IsFalse(built.Output.Contains("is not modeled", StringComparison.Ordinal), built.Output);
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("trg:D,trg2:R|r:D", await ScalarTextAsync(connectionString, states));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // init writes the states, and the scaffold publishes into another database with the same ones.
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var secondConnection = ConnectionStringFor(secondDatabase);
            var republished = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, republished.ExitCode, "publishing the scaffold failed.\n" + republished.Output);
            Assert.AreEqual("trg:D,trg2:R|r:D", await ScalarTextAsync(secondConnection, states));
            await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);

            // A project that says nothing leaves the database's states alone.
            WriteSchema(projectDirectory, Objects);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A changed state is made in place: the same trigger and the same rule.
            WriteSchema(projectDirectory, Objects + """
                ALTER TABLE public.t ENABLE ALWAYS TRIGGER trg;
                ALTER TABLE public.t ENABLE TRIGGER trg2;
                ALTER TABLE public.t ENABLE RULE r;
                """);
            var oids = await ScalarTextAsync(connectionString, "SELECT (SELECT string_agg(oid::text, ',' ORDER BY tgname) FROM pg_trigger WHERE tgrelid = 'public.t'::regclass AND NOT tgisinternal) || '|' || (SELECT oid::text FROM pg_rewrite WHERE ev_class = 'public.t'::regclass)");
            var dryRun = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.IsFalse(dryRun.Output.Contains("DROP", StringComparison.Ordinal), dryRun.Output);
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            Assert.AreEqual("trg:A,trg2:O|r:O", await ScalarTextAsync(connectionString, states));
            Assert.AreEqual(oids, await ScalarTextAsync(connectionString, "SELECT (SELECT string_agg(oid::text, ',' ORDER BY tgname) FROM pg_trigger WHERE tgrelid = 'public.t'::regclass AND NOT tgisinternal) || '|' || (SELECT oid::text FROM pg_rewrite WHERE ev_class = 'public.t'::regclass)"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(secondDatabase);
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
