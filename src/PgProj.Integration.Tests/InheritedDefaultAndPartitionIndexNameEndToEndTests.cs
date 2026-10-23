using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-237 against a real server: a child's own default on an inherited column, and a partition's index or key made on the partition first, keep their values and names through publish,
/// <c>init</c>, and a change in place.
/// </summary>
[TestClass]
public sealed class InheritedDefaultAndPartitionIndexNameEndToEndTests
{
    private const string Objects = """
        CREATE TABLE public.c0 (f1 integer, f3 integer DEFAULT 1);
        CREATE TABLE public.c1 () INHERITS (public.c0);
        ALTER TABLE ONLY public.c1 ALTER COLUMN f3 SET DEFAULT 42;
        CREATE TABLE public.p (a integer NOT NULL, b integer) PARTITION BY RANGE (a);
        CREATE TABLE public.p0 PARTITION OF public.p FOR VALUES FROM (0) TO (10);
        ALTER TABLE ONLY public.p0 ALTER COLUMN b SET DEFAULT 7;
        CREATE TABLE public.p1 PARTITION OF public.p FOR VALUES FROM (10) TO (20);
        CREATE INDEX p0_custom ON public.p0 (b);
        ALTER TABLE ONLY public.p1 ADD CONSTRAINT p1_custom_pk PRIMARY KEY (a);
        CREATE INDEX p_b ON ONLY public.p (b);
        ALTER INDEX public.p_b ATTACH PARTITION public.p0_custom;
        ALTER TABLE ONLY public.p ADD CONSTRAINT p_pkey PRIMARY KEY (a);
        ALTER INDEX public.p_pkey ATTACH PARTITION public.p1_custom_pk;
        """;

    private const string Query = "SELECT (SELECT string_agg(c.relname || '=' || coalesce(pg_get_expr(d.adbin, d.adrelid), ''), ',' ORDER BY c.relname) FROM pg_attrdef d JOIN pg_class c ON c.oid = d.adrelid WHERE c.relnamespace = 'public'::regnamespace AND c.relname IN ('c1', 'p0') AND d.adnum = (SELECT attnum FROM pg_attribute WHERE attrelid = c.oid AND attname IN ('f3', 'b'))) || '|' || (SELECT string_agg(indexname, ',' ORDER BY indexname) FROM pg_indexes WHERE schemaname = 'public')";

    [TestMethod]
    public async Task AChildDefaultAndCustomPartitionIndexNamesSurvivePublishAndInit()
    {
        var databaseName = await CreateDatabaseAsync("g237");
        var projectDirectory = CreateProjectDirectory("g237");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g237-init-" + Guid.NewGuid().ToString("N"));
        var secondDatabase = await CreateDatabaseAsync("g237b");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);

            WriteSchema(projectDirectory, Objects);
            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            Assert.IsFalse(built.Output.Contains("ignored", StringComparison.Ordinal), built.Output);
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            var state = await ScalarTextAsync(connectionString, Query);
            Assert.AreEqual("c1=42,p0=7|p0_custom,p0_pkey,p1_b_idx,p1_custom_pk,p_b,p_pkey", state);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var secondConnection = ConnectionStringFor(secondDatabase);
            var republished = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, republished.ExitCode, "publishing the scaffold failed.\n" + republished.Output);
            Assert.AreEqual(state, await ScalarTextAsync(secondConnection, Query));
            await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);

            // A changed child default is made in place.
            WriteSchema(projectDirectory, Objects.Replace("SET DEFAULT 42", "SET DEFAULT 43", StringComparison.Ordinal));
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            Assert.AreEqual("c1=43,p0=7|p0_custom,p0_pkey,p1_b_idx,p1_custom_pk,p_b,p_pkey", await ScalarTextAsync(connectionString, Query));
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
