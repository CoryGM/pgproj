using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-240 against a real server. A partition key's <c>COLLATE</c> was dropped, so a table partitioned on a case-insensitive column with <c>COLLATE "C"</c> was deployed
/// under the column's own collation, and its partitions (<c>'a', 'b'</c> and <c>'B', 'A'</c>) overlapped: <c>42P17</c>.
/// </summary>
[TestClass]
public sealed class PartitionKeyCollationEndToEndTests
{
    [TestMethod]
    public async Task APartitionKeyKeepsItsCollationAndConvergesAndIsScaffolded()
    {
        var databaseName = await CreateDatabaseAsync("g240");
        var projectDirectory = CreateProjectDirectory("g240");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g240-init-" + Guid.NewGuid().ToString("N"));
        var secondDatabase = await CreateDatabaseAsync("g240b");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, """
                CREATE COLLATION public.ci (provider = icu, locale = 'und-u-ks-level2', deterministic = false);
                CREATE TABLE public.p3 (a text, c text COLLATE public.ci) PARTITION BY LIST (c COLLATE "C");
                CREATE TABLE public.p3_1 PARTITION OF public.p3 FOR VALUES IN ('a', 'b');
                CREATE TABLE public.p3_2 PARTITION OF public.p3 FOR VALUES IN ('B', 'A');
                CREATE TABLE public.p4 (a text) PARTITION BY RANGE (a text_pattern_ops);
                CREATE TABLE public.p4_1 PARTITION OF public.p4 FOR VALUES FROM ('a') TO ('m');
                """);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            StringAssert.Contains(await ScalarTextAsync(connectionString, "SELECT pg_get_partkeydef('public.p3'::regclass)"), "COLLATE \"C\"");
            StringAssert.Contains(await ScalarTextAsync(connectionString, "SELECT pg_get_partkeydef('public.p4'::regclass)"), "text_pattern_ops");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var secondConnection = ConnectionStringFor(secondDatabase);
            var republished = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, republished.ExitCode, "publishing the scaffold failed.\n" + republished.Output);
            Assert.AreEqual(
                await ScalarTextAsync(connectionString, "SELECT pg_get_partkeydef('public.p3'::regclass) || '|' || pg_get_partkeydef('public.p4'::regclass)"),
                await ScalarTextAsync(secondConnection, "SELECT pg_get_partkeydef('public.p3'::regclass) || '|' || pg_get_partkeydef('public.p4'::regclass)"));
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
