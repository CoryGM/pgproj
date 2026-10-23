using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-215 against a real server. A materialized view's <c>WITH NO DATA</c> and storage parameters were dropped, so it was populated and had none. Both are
/// now deployed as written, a changed parameter is set in place without rebuilding the view, and a scaffold keeps both and converges.
/// </summary>
[TestClass]
public sealed class MaterializedViewStorageEndToEndTests
{
    private const string Before = """
        CREATE TABLE public.t (a int);
        CREATE MATERIALIZED VIEW public.mv WITH (fillfactor = 50, autovacuum_enabled = false) AS SELECT a FROM public.t WITH NO DATA;
        """;

    private const string After = """
        CREATE TABLE public.t (a int);
        CREATE MATERIALIZED VIEW public.mv WITH (fillfactor = 70) AS SELECT a FROM public.t WITH NO DATA;
        """;

    private const string StateQuery = "SELECT relispopulated::text || ' ' || coalesce(array_to_string(reloptions, ','), '-') FROM pg_class WHERE oid = 'public.mv'::regclass;";

    [TestMethod]
    public async Task StorageParametersAndWithNoDataAreDeployedAndChangedInPlace()
    {
        var databaseName = await CreateDatabaseAsync("g215");
        var projectDirectory = CreateProjectDirectory("g215");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g215-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Before);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("false fillfactor=50,autovacuum_enabled=false", await ScalarTextAsync(connectionString, StateQuery));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // Populated by hand; the in-place change must not rebuild the view, which would empty it again.
            await ExecuteAsync(connectionString, "INSERT INTO public.t VALUES (1); REFRESH MATERIALIZED VIEW public.mv;");
            WriteSchema(projectDirectory, After);
            var altered = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, altered.ExitCode, "the second publish failed.\n" + altered.Output);
            Assert.AreEqual("true fillfactor=70", await ScalarTextAsync(connectionString, StateQuery));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            StringAssert.Contains(File.ReadAllText(Directory.GetFiles(scaffoldDirectory, "mv.sql", SearchOption.AllDirectories).Single()), "WITH (fillfactor=70)");
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
}
