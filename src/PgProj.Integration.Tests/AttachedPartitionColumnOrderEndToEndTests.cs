using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-297 against a real server. A partition written as a standalone <c>CREATE TABLE</c> and attached later (how <c>pg_dump</c> writes it) has its own column order; it was created as
/// <c>PARTITION OF</c>, which gives it its parent's, so <c>SELECT *</c> from the partition differed from the source. Both the project and a scaffold keep the order.
/// </summary>
[TestClass]
public sealed class AttachedPartitionColumnOrderEndToEndTests
{
    private const string Schema = """
        CREATE TABLE public.parted (a integer NOT NULL, b text, c int) PARTITION BY RANGE (a);
        CREATE TABLE public.parted_1 (c int, b text, a integer NOT NULL);
        ALTER TABLE ONLY public.parted ATTACH PARTITION public.parted_1 FOR VALUES FROM (1) TO (10);
        CREATE TABLE public.parted_2 PARTITION OF public.parted FOR VALUES FROM (10) TO (20);
        CREATE TABLE public.parted_3 (a integer NOT NULL, b text, c int);
        ALTER TABLE ONLY public.parted ATTACH PARTITION public.parted_3 FOR VALUES FROM (20) TO (30);
        """;

    private const string Query = """
        SELECT string_agg(c.relname || ':' || (SELECT string_agg(a.attname, ',' ORDER BY a.attnum) FROM pg_attribute a WHERE a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped), '|' ORDER BY c.relname)
        FROM pg_class c WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('r', 'p');
        """;

    private const string Expected = "parted:a,b,c|parted_1:c,b,a|parted_2:a,b,c|parted_3:a,b,c";

    [TestMethod]
    public async Task APartitionKeepsTheColumnOrderItWasWrittenInThroughPublishAndInit()
    {
        var databaseName = await CreateDatabaseAsync("g297");
        var scaffoldDatabaseName = await CreateDatabaseAsync("g297s");
        var projectDirectory = CreateProjectDirectory("g297");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Schema);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual(Expected, await ScalarTextAsync(connectionString, Query));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var second = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.IsFalse(second.Output.Contains("parted_1", StringComparison.Ordinal), "nothing is proposed for the partition:\n" + second.Output);

            var scaffoldDirectory = Path.Combine(projectDirectory, "scaffold");
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--source-connection", connectionString, "--output", scaffoldDirectory, "--project", scaffoldProject);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            var scaffoldConnectionString = ConnectionStringFor(scaffoldDatabaseName);
            var deployedScaffold = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", scaffoldConnectionString, "--execute");
            Assert.AreEqual(0, deployedScaffold.ExitCode, deployedScaffold.Output);
            Assert.AreEqual(Expected, await ScalarTextAsync(scaffoldConnectionString, Query));
            await AssertDiffIsEmptyAsync(scaffoldProject, scaffoldConnectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await DropDatabaseAsync(scaffoldDatabaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
