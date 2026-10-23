using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-213 against a real server. <c>CREATE EXTENSION</c> lost its <c>CASCADE</c> and <c>VERSION</c>, extensions were created alphabetically, a schema
/// was forced on one written without, and an extension CASCADE installed was proposed for dropping. An extension is now deployed as written, in
/// declaration order; its stated schema, version, and comment are altered in place; and a scaffold of the result builds and converges.
/// </summary>
[TestClass]
public sealed class ExtensionEndToEndTests
{
    private const string Before = """
        CREATE SCHEMA geo;
        CREATE EXTENSION earthdistance SCHEMA geo CASCADE;
        CREATE EXTENSION citext;
        COMMENT ON EXTENSION citext IS 'ours';
        CREATE EXTENSION hstore VERSION '1.7';
        CREATE TABLE public.t (h hstore);
        """;

    private const string After = """
        CREATE SCHEMA geo;
        CREATE SCHEMA other;
        CREATE EXTENSION earthdistance SCHEMA geo CASCADE;
        CREATE EXTENSION citext SCHEMA other;
        COMMENT ON EXTENSION citext IS NULL;
        CREATE EXTENSION hstore VERSION '1.8';
        CREATE TABLE public.t (h hstore);
        """;

    private const string ExtensionsQuery = """
        SELECT string_agg(e.extname || ' ' || e.extversion || ' ' || n.nspname || ' ' || coalesce(obj_description(e.oid, 'pg_extension'), '-'), '; ' ORDER BY e.extname)
        FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace WHERE e.extname <> 'plpgsql';
        """;

    [TestMethod]
    public async Task ExtensionsAreDeployedAsWrittenAlteredInPlaceAndScaffolded()
    {
        var databaseName = await CreateDatabaseAsync("g213");
        var projectDirectory = CreateProjectDirectory("g213");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g213-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Before);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            var installed = await ScalarTextAsync(connectionString, ExtensionsQuery);
            StringAssert.Contains(installed, "citext ");
            StringAssert.Contains(installed, " public ours");
            StringAssert.Contains(installed, "cube 1.5 geo");
            StringAssert.Contains(installed, "hstore 1.7 public");

            // cube was installed by CASCADE and is not the project's, but earthdistance needs it, so it is not proposed for dropping.
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            WriteSchema(projectDirectory, After);
            var altered = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, altered.ExitCode, "the second publish failed.\n" + altered.Output);
            installed = await ScalarTextAsync(connectionString, ExtensionsQuery);
            StringAssert.Contains(installed, " other -");
            StringAssert.Contains(installed, "hstore 1.8 public");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var cubeFile = File.ReadAllText(Directory.GetFiles(scaffoldDirectory, "cube.sql", SearchOption.AllDirectories).Single());
            Assert.IsTrue(cubeFile.IndexOf("CREATE EXTENSION cube", StringComparison.Ordinal) < cubeFile.IndexOf("CREATE EXTENSION earthdistance", StringComparison.Ordinal), cubeFile);
            StringAssert.Contains(File.ReadAllText(Directory.GetFiles(scaffoldDirectory, "citext.sql", SearchOption.AllDirectories).Single()), "COMMENT ON EXTENSION citext IS NULL;");
            var scaffoldBuilt = await RunAsync("build", "--project", scaffoldProject);
            Assert.AreEqual(0, scaffoldBuilt.ExitCode, scaffoldBuilt.Output);
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
    /// G-218: moving an extension carries its types with it, so a column of one is the same column, and the move needs no <c>--allow-destructive</c>.
    /// </summary>
    [TestMethod]
    public async Task MovingAnExtensionLeavesTheColumnsOfItsTypesAlone()
    {
        var databaseName = await CreateDatabaseAsync("g218");
        var projectDirectory = CreateProjectDirectory("g218");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, "CREATE EXTENSION citext;\nCREATE TABLE public.t (a citext, b citext[]);\n");
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            await ExecuteAsync(connectionString, "INSERT INTO public.t VALUES ('Kept', ARRAY['x']);");

            WriteSchema(projectDirectory, "CREATE SCHEMA other;\nCREATE EXTENSION citext SCHEMA other;\nCREATE TABLE public.t (a other.citext, b other.citext[]);\n");
            var moved = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, moved.ExitCode, "the move was refused or failed.\n" + moved.Output);
            Assert.AreEqual("other", await ScalarTextAsync(connectionString, "SELECT n.nspname FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace WHERE e.extname = 'citext';"));
            Assert.AreEqual("1", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM public.t WHERE a::text = 'Kept';"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    /// <summary>
    /// A project scoped to its schemas: an extension it declares without <c>SCHEMA</c> lands outside them and is still compared, since an extension is
    /// named by its name alone; one it does not declare outside them belongs to someone else and is left alone.
    /// </summary>
    [TestMethod]
    public async Task AScopedProjectComparesTheExtensionsItNamesWhereverTheyAre()
    {
        var databaseName = await CreateDatabaseAsync("g213s");
        var projectDirectory = CreateProjectDirectory("g213s", """, "schemas": ["app"]""");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            await ExecuteAsync(connectionString, "CREATE EXTENSION hstore;");
            WriteSchema(projectDirectory, "CREATE SCHEMA app;\nCREATE EXTENSION citext;\nCREATE TABLE app.t (a citext);\n");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("citext hstore", await ScalarTextAsync(connectionString, "SELECT string_agg(extname, ' ' ORDER BY extname) FROM pg_extension WHERE extname <> 'plpgsql';"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
