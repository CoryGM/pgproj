using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-252 against a real server: several foreign keys of one table to a partitioned table, with the numbered names a source database ends up with, publish without a name collision, converge,
/// and survive <c>init</c>. Before the fix PostgreSQL 16 and 17 refused with <c>42710</c> because a clone made for <c>fk_a_fkey12</c> took the name <c>fk_a_fkey4</c>.
/// </summary>
[TestClass]
public sealed class ForeignKeyNumberOrderEndToEndTests
{
    private const string Objects = """
        CREATE TABLE public.pk (a integer PRIMARY KEY) PARTITION BY LIST (a);
        CREATE TABLE public.pk1 PARTITION OF public.pk FOR VALUES IN (1);
        CREATE TABLE public.pk2 PARTITION OF public.pk FOR VALUES IN (2);
        CREATE TABLE public.pk3 PARTITION OF public.pk FOR VALUES IN (3);
        CREATE TABLE public.fk (a integer);
        ALTER TABLE public.fk ADD CONSTRAINT fk_a_fkey FOREIGN KEY (a) REFERENCES public.pk (a) ON UPDATE CASCADE ON DELETE CASCADE;
        ALTER TABLE public.fk ADD CONSTRAINT fk_a_fkey12 FOREIGN KEY (a) REFERENCES public.pk (a) ON UPDATE RESTRICT ON DELETE RESTRICT;
        ALTER TABLE public.fk ADD CONSTRAINT fk_a_fkey4 FOREIGN KEY (a) REFERENCES public.pk (a) ON UPDATE SET NULL ON DELETE SET NULL;
        """;

    private const string Query = "SELECT string_agg(conname, ',' ORDER BY conname) FROM pg_constraint WHERE conrelid = 'public.fk'::regclass AND contype = 'f' AND conparentid = 0";

    [TestMethod]
    public async Task NumberedForeignKeysToAPartitionedTablePublishConvergeAndRoundTrip()
    {
        var databaseName = await CreateDatabaseAsync("g252");
        var projectDirectory = CreateProjectDirectory("g252");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g252-init-" + Guid.NewGuid().ToString("N"));
        var secondDatabase = await CreateDatabaseAsync("g252b");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            const string expected = "fk_a_fkey,fk_a_fkey12,fk_a_fkey4";

            WriteSchema(projectDirectory, Objects);
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual(expected, await ScalarTextAsync(connectionString, Query));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var secondConnection = ConnectionStringFor(secondDatabase);
            var republished = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, republished.ExitCode, "publishing the scaffold failed.\n" + republished.Output);
            Assert.AreEqual(expected, await ScalarTextAsync(secondConnection, Query));
            await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);
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
