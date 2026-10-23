using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-266 against a real server. <c>CREATE TABLE child PARTITION OF parent (...)</c> with column options and table constraints failed <c>build</c> or lost its NOT NULL. The list is
/// deployed as written, the partition's default, NOT NULL (with its name on PostgreSQL 18), and CHECK are in the database, the project converges, a change to the list's default is
/// made in place, and <c>init</c> (which writes the partition its own way) round-trips into a second database.
/// </summary>
[TestClass]
public sealed class PartitionOfElementListEndToEndTests
{
    private const string Schema = """
        CREATE TABLE public.p (partid integer NOT NULL, a integer, b integer DEFAULT 1) PARTITION BY RANGE (partid);
        CREATE TABLE public.c PARTITION OF public.p (
            b WITH OPTIONS DEFAULT 5,
            a CONSTRAINT c_an NOT NULL,
            CONSTRAINT c_ck CHECK (b > 0)
        ) FOR VALUES FROM (1) TO (10);
        CREATE TABLE public.d PARTITION OF public.p (b CONSTRAINT d_bn NOT NULL) FOR VALUES FROM (10) TO (20);
        """;

    /// <summary>A NOT NULL written as a table constraint in the list: only PostgreSQL 18 accepts the form.</summary>
    private const string TableNotNull = "CREATE TABLE public.e PARTITION OF public.p (CONSTRAINT e_bn NOT NULL b) FOR VALUES FROM (20) TO (30);";

    [TestMethod]
    public async Task APartitionsElementListIsDeployedAndConverges()
    {
        var databaseName = await CreateDatabaseAsync("g266");
        var secondDatabase = await CreateDatabaseAsync("g266b");
        var projectDirectory = CreateProjectDirectory("g266");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g266-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Schema);

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            Assert.IsFalse(built.Output.Contains("[Warning]", StringComparison.Ordinal), built.Output);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("5", await DefaultOf(connectionString, "c", "b"));
            Assert.AreEqual("1", await DefaultOf(connectionString, "d", "b"), "A partition without its own default has the parent's.");
            Assert.AreEqual("t", await ScalarTextAsync(connectionString, "SELECT attnotnull::text FROM pg_attribute WHERE attrelid = 'public.c'::regclass AND attname = 'a'").ContinueWith(static task => task.Result[..1]));
            Assert.AreEqual("t", await ScalarTextAsync(connectionString, "SELECT attnotnull::text FROM pg_attribute WHERE attrelid = 'public.d'::regclass AND attname = 'b'").ContinueWith(static task => task.Result[..1]));
            Assert.AreEqual("1", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_constraint WHERE conrelid = 'public.c'::regclass AND conname = 'c_ck'"));
            if (int.Parse(await ScalarTextAsync(connectionString, "SHOW server_version_num"), System.Globalization.CultureInfo.InvariantCulture) >= 180000)
            {
                Assert.AreEqual("c_an", await ScalarTextAsync(connectionString, "SELECT conname FROM pg_constraint WHERE conrelid = 'public.c'::regclass AND contype = 'n' AND conkey = ARRAY[(SELECT attnum FROM pg_attribute WHERE attrelid = 'public.c'::regclass AND attname = 'a')]"));
                Assert.AreEqual("d_bn", await ScalarTextAsync(connectionString, "SELECT conname FROM pg_constraint WHERE conrelid = 'public.d'::regclass AND contype = 'n' AND conkey = ARRAY[(SELECT attnum FROM pg_attribute WHERE attrelid = 'public.d'::regclass AND attname = 'b')]"));
            }

            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // init writes the partitions its own way (ALTER TABLE ONLY ... SET DEFAULT; on 18, created and attached for its own NOT NULL names); that publishes and converges.
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);
            var secondConnection = ConnectionStringFor(secondDatabase);
            var republished = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, republished.ExitCode, "publishing the scaffold failed.\n" + republished.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);
            await AssertDiffIsEmptyAsync(projectFile, secondConnection);

            // A changed default in the list is made in place, without recreating the partition.
            var oid = await ScalarTextAsync(connectionString, "SELECT 'public.c'::regclass::oid::text");
            WriteSchema(projectDirectory, Edit(Schema, "b WITH OPTIONS DEFAULT 5", "b WITH OPTIONS DEFAULT 6"));
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            Assert.AreEqual("6", await DefaultOf(connectionString, "c", "b"));
            Assert.AreEqual(oid, await ScalarTextAsync(connectionString, "SELECT 'public.c'::regclass::oid::text"));
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

    [TestMethod]
    public async Task ATableNotNullInTheListIsDeployedOn18AndRefusedByNameBefore()
    {
        var databaseName = await CreateDatabaseAsync("g266t");
        var projectDirectory = CreateProjectDirectory("g266t");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Schema + TableNotNull);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            if (int.Parse(await ScalarTextAsync(connectionString, "SHOW server_version_num"), System.Globalization.CultureInfo.InvariantCulture) < 180000)
            {
                Assert.AreEqual(1, published.ExitCode, published.Output);
                StringAssert.Contains(published.Output, "table constraint NOT NULL in the PARTITION OF list of 'public.e' (column b)");
                Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_class WHERE relname = 'p'"), "Nothing ran.");
                return;
            }

            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("e_bn", await ScalarTextAsync(connectionString, "SELECT conname FROM pg_constraint WHERE conrelid = 'public.e'::regclass AND contype = 'n' AND conkey = ARRAY[(SELECT attnum FROM pg_attribute WHERE attrelid = 'public.e'::regclass AND attname = 'b')]"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    private static Task<string> DefaultOf(string connectionString, string table, string column)
        => ScalarTextAsync(connectionString, $"SELECT pg_get_expr(d.adbin, d.adrelid) FROM pg_attrdef d JOIN pg_attribute a ON a.attrelid = d.adrelid AND a.attnum = d.adnum WHERE d.adrelid = 'public.{table}'::regclass AND a.attname = '{column}'");
}
