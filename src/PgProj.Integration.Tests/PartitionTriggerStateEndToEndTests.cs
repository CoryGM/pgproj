using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-293 against a real server. A row trigger of a partitioned table is cloned onto each partition, and a partition's copy can be disabled or set
/// <c>ENABLE ALWAYS</c> or <c>REPLICA</c> on its own. The state of a copy was not read and the statement was warned about and ignored.
/// </summary>
[TestClass]
public sealed class PartitionTriggerStateEndToEndTests
{
    private const string Tables = """
        CREATE FUNCTION public.trg_fn() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;
        CREATE TABLE public.parted (a int) PARTITION BY RANGE (a);
        CREATE TABLE public.parted_p1 PARTITION OF public.parted FOR VALUES FROM (1) TO (10);
        CREATE TABLE public.parted_p2 PARTITION OF public.parted FOR VALUES FROM (10) TO (20) PARTITION BY RANGE (a);
        CREATE TABLE public.parted_p2_2 PARTITION OF public.parted_p2 FOR VALUES FROM (10) TO (15);
        CREATE TRIGGER aft_row AFTER INSERT ON public.parted FOR EACH ROW EXECUTE FUNCTION public.trg_fn();
        """;

    private const string Before = Tables + """
        ALTER TABLE public.parted_p2_2 ENABLE ALWAYS TRIGGER aft_row;
        ALTER TABLE public.parted_p2 DISABLE TRIGGER aft_row;
        """;

    // The copy on p2 is stated nowhere, so it keeps what the database has; p1 and p2_2 change.
    private const string After = Tables + """
        ALTER TABLE public.parted_p1 ENABLE REPLICA TRIGGER aft_row;
        ALTER TABLE public.parted_p2_2 DISABLE TRIGGER aft_row;
        """;

    private const string StatesQuery = """
        SELECT string_agg(c.relname || '=' || t.tgenabled::text, ' ' ORDER BY c.relname)
        FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid WHERE t.tgname = 'aft_row';
        """;

    [TestMethod]
    public async Task APartitionsCopyOfATriggerKeepsItsOwnFiringMode()
    {
        var databaseName = await CreateDatabaseAsync("g293");
        var secondDatabaseName = await CreateDatabaseAsync("g293b");
        var projectDirectory = CreateProjectDirectory("g293");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g293-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Before);

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            Assert.IsFalse(built.Output.Contains("does not declare", StringComparison.Ordinal), "the states are modeled:\n" + built.Output);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("parted=O parted_p1=O parted_p2=D parted_p2_2=A", await ScalarTextAsync(connectionString, StatesQuery));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            WriteSchema(projectDirectory, After);
            var diff = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            StringAssert.Contains(diff.Output, "Partition states");
            var altered = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, altered.ExitCode, "the second publish failed.\n" + altered.Output);
            Assert.AreEqual("parted=O parted_p1=R parted_p2=D parted_p2_2=D", await ScalarTextAsync(connectionString, StatesQuery));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var scaffoldBuilt = await RunAsync("build", "--project", scaffoldProject);
            Assert.AreEqual(0, scaffoldBuilt.ExitCode, scaffoldBuilt.Output);
            Assert.IsFalse(scaffoldBuilt.Output.Contains("does not declare", StringComparison.Ordinal), scaffoldBuilt.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            var secondConnection = ConnectionStringFor(secondDatabaseName);
            var scaffoldPublished = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, scaffoldPublished.ExitCode, scaffoldPublished.Output);
            Assert.AreEqual("parted=O parted_p1=R parted_p2=D parted_p2_2=D", await ScalarTextAsync(secondConnection, StatesQuery));
            await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await DropDatabaseAsync(secondDatabaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
