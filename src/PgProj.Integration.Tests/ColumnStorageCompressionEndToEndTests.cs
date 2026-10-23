using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-222 against a real server. A column's <c>STORAGE</c> and <c>COMPRESSION</c> were dropped, so the columns got their type's storage and the default compression.
/// They are now deployed as written, converge, are changed in place, and are scaffolded.
/// </summary>
[TestClass]
public sealed class ColumnStorageCompressionEndToEndTests
{
    private const string Columns = "SELECT string_agg(attname || ':' || attstorage::text || ':' || attcompression::text, ',' ORDER BY attnum) FROM pg_attribute WHERE attrelid = 'public.t'::regclass AND attnum > 0;";

    [TestMethod]
    [PostgreSqlVersion(Minimum = 16)]
    public async Task StorageAndCompressionAreDeployedChangedInPlaceAndScaffolded()
    {
        var databaseName = await CreateDatabaseAsync("g222");
        var projectDirectory = CreateProjectDirectory("g222");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g222-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, "CREATE TABLE public.t (a text STORAGE MAIN COMPRESSION pglz, b text COMPRESSION default, c text STORAGE default, d text);\n");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("a:m:p,b:x:,c:x:,d:x:", await ScalarTextAsync(connectionString, Columns));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            await ExecuteAsync(connectionString, "INSERT INTO public.t VALUES ('x', 'y', 'z', 'w');");
            WriteSchema(projectDirectory, "CREATE TABLE public.t (a text STORAGE external COMPRESSION default, b text, c text STORAGE main, d text);\n");
            var altered = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, altered.ExitCode, "the second publish failed.\n" + altered.Output);
            Assert.AreEqual("a:e:,b:x:,c:m:,d:x:", await ScalarTextAsync(connectionString, Columns));
            Assert.AreEqual("1", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM public.t;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var tableFile = File.ReadAllText(Directory.GetFiles(scaffoldDirectory, "t.sql", SearchOption.AllDirectories).Single());
            // The scaffold sets the storage with ALTER TABLE, which every supported version accepts (G-269).
            StringAssert.Contains(tableFile, "ALTER TABLE public.t ALTER COLUMN a SET STORAGE external;");
            StringAssert.Contains(tableFile, "ALTER TABLE public.t ALTER COLUMN c SET STORAGE main;");
            Assert.IsFalse(tableFile.Contains("STORAGE extended", StringComparison.Ordinal), tableFile);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }

    /// <summary>
    /// G-269: <c>STORAGE</c> inside a column's definition is PostgreSQL 16 syntax, so on 14 and 15 <c>publish --execute</c> refuses it by name before anything runs; the same
    /// storage set with <c>ALTER TABLE ... SET STORAGE</c> is deployed that way and converges.
    /// </summary>
    [TestMethod]
    [PostgreSqlVersion(Maximum = 15)]
    public async Task StorageInAColumnDefinitionIsRefusedBefore16AndTheAlterFormIsDeployed()
    {
        var databaseName = await CreateDatabaseAsync("g269s");
        var projectDirectory = CreateProjectDirectory("g269s");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, "CREATE TABLE public.t (a text STORAGE main, b text);\n");

            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(1, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "STORAGE in the definition of column 'public.t.a' (needs PostgreSQL 16)");
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_class WHERE relname = 't' AND relnamespace = 'public'::regnamespace"), "Nothing ran.");

            WriteSchema(projectDirectory, "CREATE TABLE public.t (a text, b text);\nALTER TABLE public.t ALTER COLUMN a SET STORAGE main;\n");
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("m", await ScalarTextAsync(connectionString, "SELECT attstorage::text FROM pg_attribute WHERE attrelid = 'public.t'::regclass AND attname = 'a'"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
