using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-264 against a PostgreSQL 18 server: partitions made first with their own NOT NULL constraint names and attached later keep the names through <c>publish</c> and <c>init</c>, and a partition made
/// with <c>PARTITION OF</c> keeps the parent's.
/// </summary>
[TestClass]
public sealed class PartitionOwnNotNullNameEndToEndTests
{
    private const string Objects = """
        CREATE TABLE public.parent (partid integer NOT NULL, b integer) PARTITION BY RANGE (partid);
        CREATE TABLE public.attached (partid integer CONSTRAINT attached_partid_not_null NOT NULL, b integer);
        ALTER TABLE public.parent ATTACH PARTITION public.attached FOR VALUES FROM (1) TO (10);
        CREATE TABLE public.renamed (partid integer CONSTRAINT mine NOT NULL, b integer);
        ALTER TABLE public.parent ATTACH PARTITION public.renamed FOR VALUES FROM (10) TO (20);
        CREATE TABLE public.plain PARTITION OF public.parent FOR VALUES FROM (20) TO (30);
        """;

    private const string Query = "SELECT string_agg(c.relname || ':' || k.conname || ':' || k.conislocal::text, ',' ORDER BY c.relname) FROM pg_class c JOIN pg_constraint k ON k.conrelid = c.oid AND k.contype = 'n' "
        + "WHERE c.relnamespace = 'public'::regnamespace AND c.relkind IN ('r', 'p')";

    [TestMethod]
    [PostgreSqlVersion(Minimum = 18)]
    public async Task APartitionKeepsItsOwnNotNullNamesThroughPublishAndInit()
    {
        var databaseName = await CreateDatabaseAsync("g264");
        var secondDatabase = await CreateDatabaseAsync("g264b");
        var projectDirectory = CreateProjectDirectory("g264");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g264-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Objects);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            var state = await ScalarTextAsync(connectionString, Query);
            StringAssert.Contains(state, "attached:attached_partid_not_null:");
            StringAssert.Contains(state, "renamed:mine:");
            StringAssert.Contains(state, "plain:parent_partid_not_null:");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A second publish changes nothing.
            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, again.ExitCode, again.Output);

            // Round trip through init: the scaffold deploys the same names into another database.
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--source-connection", connectionString, "--output", scaffoldDirectory, "--project", scaffoldProject);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var scaffoldedSql = string.Concat(Directory.EnumerateFiles(scaffoldDirectory, "*.sql", SearchOption.AllDirectories).Order().Select(File.ReadAllText));
            StringAssert.Contains(scaffoldedSql, "CONSTRAINT mine NOT NULL");
            StringAssert.Contains(scaffoldedSql, "CREATE TABLE public.plain PARTITION OF public.parent");
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            var secondConnection = ConnectionStringFor(secondDatabase);
            var deployed = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, deployed.ExitCode, deployed.Output);
            Assert.AreEqual(state, await ScalarTextAsync(secondConnection, Query));
            await AssertDiffIsEmptyAsync(projectFile, secondConnection);
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
