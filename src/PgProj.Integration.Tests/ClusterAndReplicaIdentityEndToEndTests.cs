using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-255 and G-256 against a real server: <c>CLUSTER ON</c> and <c>REPLICA IDENTITY</c> are deployed, read back by <c>init</c>, compared only when stated, and changed in place.
/// </summary>
[TestClass]
public sealed class ClusterAndReplicaIdentityEndToEndTests
{
    private const string Objects = """
        CREATE TABLE public.a (id integer PRIMARY KEY, code text NOT NULL, note text);
        CREATE UNIQUE INDEX a_code ON public.a (code);
        CREATE INDEX a_note ON public.a (note);
        CREATE TABLE public.b (id integer);
        """;

    private const string Query = "SELECT (SELECT string_agg(c.relname, ',' ORDER BY c.relname) FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid WHERE i.indisclustered) || '|' || (SELECT string_agg(c.relname, ',' ORDER BY c.relname) FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid WHERE i.indisreplident) || '|' || (SELECT string_agg(relname || ':' || relreplident::text, ',' ORDER BY relname) FROM pg_class WHERE relnamespace = 'public'::regnamespace AND relkind = 'r')";

    /// <summary>G-260: a project that says <c>SET WITHOUT CLUSTER</c> unclusters a clustered table; one that says nothing leaves it alone.</summary>
    [TestMethod]
    public async Task SetWithoutClusterUnclustersAClusteredTable()
    {
        var databaseName = await CreateDatabaseAsync("g260");
        var projectDirectory = CreateProjectDirectory("g260");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            const string clustered = "SELECT count(*) FROM pg_index WHERE indisclustered";

            WriteSchema(projectDirectory, Objects + "ALTER TABLE public.a CLUSTER ON a_note;");
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("1", await ScalarTextAsync(connectionString, clustered));

            // Saying nothing leaves the clustering alone.
            WriteSchema(projectDirectory, Objects);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
            Assert.AreEqual("1", await ScalarTextAsync(connectionString, clustered));

            // Saying SET WITHOUT CLUSTER removes it, in place, and then converges.
            WriteSchema(projectDirectory, Objects + "ALTER TABLE public.a SET WITHOUT CLUSTER;");
            var built = await RunAsync("build", "--project", projectFile);
            Assert.IsFalse(built.Output.Contains("is not modeled", StringComparison.Ordinal), built.Output);
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, clustered));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task ClusterOnAndReplicaIdentityAreDeployedScaffoldedAndChangedInPlace()
    {
        var databaseName = await CreateDatabaseAsync("g255");
        var projectDirectory = CreateProjectDirectory("g255");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g255-init-" + Guid.NewGuid().ToString("N"));
        var secondDatabase = await CreateDatabaseAsync("g255b");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);

            WriteSchema(projectDirectory, Objects + """
                ALTER TABLE public.a CLUSTER ON a_note;
                ALTER TABLE public.a REPLICA IDENTITY USING INDEX a_code;
                ALTER TABLE public.b REPLICA IDENTITY FULL;
                """);
            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            Assert.IsFalse(built.Output.Contains("is not modeled", StringComparison.Ordinal), built.Output);
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            const string expected = "a_note|a_code|a:i,b:f";
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

            // A project that says nothing leaves the database's settings alone.
            WriteSchema(projectDirectory, Objects);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // Changed settings, including a primary key, are made in place.
            WriteSchema(projectDirectory, Objects + """
                ALTER TABLE public.a CLUSTER ON a_pkey;
                ALTER TABLE public.a REPLICA IDENTITY USING INDEX a_pkey;
                ALTER TABLE public.b REPLICA IDENTITY NOTHING;
                """);
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            Assert.AreEqual("a_pkey|a_pkey|a:i,b:n", await ScalarTextAsync(connectionString, Query));
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
