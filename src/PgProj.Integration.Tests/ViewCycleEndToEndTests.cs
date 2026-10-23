using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-254 against a real server: views that refer to each other (made possible by <c>CREATE OR REPLACE VIEW</c>) are published from a project that writes the cycle either way, converge, and
/// survive <c>init</c>, which breaks the cycle with the placeholder <c>pg_dump</c> uses.
/// </summary>
[TestClass]
public sealed class ViewCycleEndToEndTests
{
    private const string Tables = """
        CREATE TABLE public.t1 (a bigint);
        CREATE TABLE public.t1a (b bigint);
        """;

    private const string EarlierBody = Tables + """
        CREATE VIEW public.v2(a, b) AS SELECT * FROM public.t1, public.t1a;
        CREATE VIEW public.v3 AS SELECT * FROM public.v2;
        CREATE OR REPLACE VIEW public.v2 AS SELECT * FROM public.v3;
        CREATE VIEW public.v4 AS SELECT * FROM public.v3;
        COMMENT ON VIEW public.v2 IS 'in a cycle';
        """;

    private const string DumpPlaceholder = Tables + """
        CREATE VIEW public.v2 AS SELECT NULL::bigint AS a, NULL::bigint AS b;
        CREATE VIEW public.v3 AS SELECT a, b FROM public.v2;
        CREATE OR REPLACE VIEW public.v2 AS SELECT a, b FROM public.v3;
        """;

    private const string Query = "SELECT string_agg(c.relname || ':' || (SELECT string_agg(a.attname, '+' ORDER BY a.attnum) FROM pg_attribute a WHERE a.attrelid = c.oid AND a.attnum > 0), ',' ORDER BY c.relname) FROM pg_class c WHERE c.relnamespace = 'public'::regnamespace AND c.relkind = 'v'";

    [TestMethod]
    public async Task ViewsThatReadEachOtherPublishConvergeAndRoundTripThroughInit()
    {
        var databaseName = await CreateDatabaseAsync("g254");
        var projectDirectory = CreateProjectDirectory("g254");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g254-init-" + Guid.NewGuid().ToString("N"));
        var secondDatabase = await CreateDatabaseAsync("g254b");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);

            WriteSchema(projectDirectory, EarlierBody);
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("v2:a+b,v3:a+b,v4:a+b", await ScalarTextAsync(connectionString, Query));
            Assert.AreEqual("in a cycle", await ScalarTextAsync(connectionString, "SELECT obj_description('public.v2'::regclass, 'pg_class')"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // The scaffold writes the placeholder, and publishes into another database.
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var secondConnection = ConnectionStringFor(secondDatabase);
            var republished = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, republished.ExitCode, "publishing the scaffold failed.\n" + republished.Output);
            Assert.AreEqual("v2:a+b,v3:a+b,v4:a+b", await ScalarTextAsync(secondConnection, Query));
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

    [TestMethod]
    public async Task APgDumpStylePlaceholderProjectPublishesAndConverges()
    {
        var databaseName = await CreateDatabaseAsync("g254p");
        var projectDirectory = CreateProjectDirectory("g254p");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);

            WriteSchema(projectDirectory, DumpPlaceholder);
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("v2:a+b,v3:a+b", await ScalarTextAsync(connectionString, Query));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
