using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-148 against a real server, on every version: collations (an ICU one that is not deterministic, a libc one, and one nothing uses) deploy before the column and the index that
/// use them whatever order the file has, read back from <c>pg_collation</c>, converge <c>0/0/0</c>, round-trip through <c>init</c> into a file of their own, and are changed or removed
/// safely: one nothing uses is dropped and created again, one something uses is refused with a message instead of failing halfway.
/// </summary>
[TestClass]
public sealed class CollationEndToEndTests
{
    private static string Schema(string unusedLocale = "de", bool withUnused = true)
        => $"""
            CREATE TABLE app.t (id int PRIMARY KEY, n text COLLATE app.ci, m text COLLATE app.c_posix);
            CREATE INDEX t_m ON app.t (m COLLATE app.ci);
            CREATE COLLATION app.ci (provider = icu, locale = 'und-u-ks-level2', deterministic = false);
            CREATE COLLATION app.c_posix (locale = 'C');
            {(withUnused ? $"CREATE COLLATION app.unused (provider = icu, locale = '{unusedLocale}');" : string.Empty)}
            CREATE SCHEMA app;
            """;

    private const string Query = """
        SELECT string_agg(c.collname || ':' || CASE c.collprovider::text WHEN 'i' THEN 'icu' ELSE 'libc' END || ':' || c.collisdeterministic::text, '|' ORDER BY c.collname)
        FROM pg_collation c JOIN pg_namespace n ON n.oid = c.collnamespace WHERE n.nspname = 'app';
        """;

    [TestMethod]
    public async Task CollationsDeployConvergeRoundTripThroughInitAndAreChangedOrRemovedSafely()
    {
        var databaseName = await CreateDatabaseAsync("g148");
        var scaffoldDatabaseName = await CreateDatabaseAsync("g148s");
        var projectDirectory = CreateProjectDirectory("g148");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Schema());

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            StringAssert.Contains(built.Output, "No diagnostics.");

            // The table and the index come before the collations in the file, and still deploy after them.
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("c_posix:libc:true|ci:icu:false|unused:icu:true", await ScalarTextAsync(connectionString, Query));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, again.ExitCode, again.Output);
            StringAssert.Contains(again.Output, "Executed deployment script.");
            var second = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.IsFalse(second.Output.Contains("Collation", StringComparison.Ordinal), second.Output);

            // Round trip through init: each collation is in a file of its own, and the scaffold deploys the same into a second database.
            var scaffoldDirectory = Path.Combine(projectDirectory, "scaffold");
            var scaffolded = await RunAsync("init", "--source-connection", connectionString, "--output", scaffoldDirectory, "--project", Path.Combine(scaffoldDirectory, "pgproj.json"));
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            Assert.AreEqual(3, Directory.GetFiles(Path.Combine(scaffoldDirectory, "Schema", "app", "Collation"), "*.sql").Length);
            await AssertDiffIsEmptyAsync(Path.Combine(scaffoldDirectory, "pgproj.json"), connectionString);
            var scaffoldConnectionString = ConnectionStringFor(scaffoldDatabaseName);
            var deployedScaffold = await RunAsync("publish", "--project", Path.Combine(scaffoldDirectory, "pgproj.json"), "--target-connection", scaffoldConnectionString, "--execute");
            Assert.AreEqual(0, deployedScaffold.ExitCode, deployedScaffold.Output);
            Assert.AreEqual("c_posix:libc:true|ci:icu:false|unused:icu:true", await ScalarTextAsync(scaffoldConnectionString, Query));
            await AssertDiffIsEmptyAsync(projectFile, scaffoldConnectionString);

            // A collation nothing uses is changed by a drop and a create.
            WriteSchema(projectDirectory, Schema(unusedLocale: "fr"));
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            Assert.AreEqual("fr", await ScalarTextAsync(connectionString, "SELECT coalesce(to_jsonb(c) ->> 'colllocale', to_jsonb(c) ->> 'colliculocale', case when c.collprovider = 'i' then to_jsonb(c) ->> 'collcollate' end) FROM pg_collation c WHERE collname = 'unused';"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // One something uses is not changed: PostgreSQL refuses to drop it, so pgproj says so before running anything.
            WriteSchema(projectDirectory, Schema(unusedLocale: "fr").Replace("und-u-ks-level2", "und-u-ks-level1", StringComparison.Ordinal));
            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(1, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "[Collation] app.ci");
            var preview = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            StringAssert.Contains(preview.Output, "-- TODO: The collation app.ci changed");
            StringAssert.Contains(preview.Output, "t");
            Assert.AreEqual("und-u-ks-level2", await ScalarTextAsync(connectionString, "SELECT coalesce(to_jsonb(c) ->> 'colllocale', to_jsonb(c) ->> 'colliculocale', case when c.collprovider = 'i' then to_jsonb(c) ->> 'collcollate' end) FROM pg_collation c WHERE collname = 'ci';"));

            // One nothing uses is removed without --allow-destructive: it holds no data.
            WriteSchema(projectDirectory, Schema(withUnused: false));
            var removed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, removed.ExitCode, removed.Output);
            Assert.AreEqual("c_posix:libc:true|ci:icu:false", await ScalarTextAsync(connectionString, Query));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await DropDatabaseAsync(scaffoldDatabaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    /// <summary>
    /// G-155: PostgreSQL prints no <c>COLLATE</c> on an index key that equals its column's, so a project that repeats the column's collation on the key (here a project
    /// collation, written once qualified and once not) used to differ from the database after every publish.
    /// </summary>
    [TestMethod]
    public async Task AnIndexKeyRepeatingItsColumnsCollationConverges()
    {
        var databaseName = await CreateDatabaseAsync("g155");
        var projectDirectory = CreateProjectDirectory("g155");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, """
                CREATE SCHEMA app;
                CREATE COLLATION app.ci (provider = icu, locale = 'und-u-ks-level2', deterministic = false);
                CREATE TABLE app.t (id int PRIMARY KEY, n text COLLATE app.ci, m text COLLATE app.ci, o text);
                CREATE INDEX t_n ON app.t (n COLLATE app.ci);
                CREATE INDEX t_m ON app.t (m COLLATE ci, id);
                CREATE INDEX t_o ON app.t (o COLLATE "C");
                """);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.IsFalse(again.Output.Contains("CREATE INDEX", StringComparison.Ordinal) || again.Output.Contains("DROP INDEX", StringComparison.Ordinal), again.Output);

            // The key on a column with another collation still keeps its own: that index is on o with "C", which the column does not have.
            Assert.AreEqual("C", await ScalarTextAsync(connectionString, "SELECT c.collname FROM pg_index i JOIN pg_collation c ON c.oid = i.indcollation[0] WHERE i.indexrelid = 'app.t_o'::regclass;"));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    /// <summary>
    /// G-156: the database names a built-in collation <c>pg_catalog."C"</c> and a project writes <c>"C"</c>; a column and an index key written with it used to differ from
    /// the database after every publish.
    /// </summary>
    [TestMethod]
    public async Task ABuiltInCollationOnAColumnConverges()
    {
        var databaseName = await CreateDatabaseAsync("g156");
        var projectDirectory = CreateProjectDirectory("g156");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, """
                CREATE SCHEMA app;
                CREATE TABLE app.t (id int PRIMARY KEY, m text COLLATE "C", n text COLLATE "POSIX", o text COLLATE "default", p text);
                CREATE INDEX t_m ON app.t (m COLLATE "C");
                CREATE INDEX t_p ON app.t (p COLLATE "C");
                """);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.IsFalse(again.Output.Contains("ALTER TABLE", StringComparison.Ordinal) || again.Output.Contains("CREATE INDEX", StringComparison.Ordinal), again.Output);

            // A different collation is still a change.
            WriteSchema(projectDirectory, """
                CREATE SCHEMA app;
                CREATE TABLE app.t (id int PRIMARY KEY, m text COLLATE "POSIX", n text COLLATE "POSIX", o text COLLATE "default", p text);
                CREATE INDEX t_m ON app.t (m COLLATE "C");
                CREATE INDEX t_p ON app.t (p COLLATE "C");
                """);
            var changed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            StringAssert.Contains(changed.Output, "Collation: ");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task AProjectThatListsSchemasOnlyManagesTheCollationsInThem()
    {
        var databaseName = await CreateDatabaseAsync("g148sc");
        var projectDirectory = CreateProjectDirectory("g148sc", ", \"schemas\": [\"app\"]");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, "CREATE COLLATION public.other (locale = 'C');");
            WriteSchema(projectDirectory, "CREATE SCHEMA app; CREATE COLLATION app.mine (locale = 'C');");

            var published = await RunAsync("publish", "--project", ProjectFile(projectDirectory), "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("2", await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_collation WHERE oid >= 16384;"));
            await AssertDiffIsEmptyAsync(ProjectFile(projectDirectory), connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
