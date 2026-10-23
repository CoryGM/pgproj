using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-216 against a real server. <c>COMMENT ON COLUMN</c> on a view, a materialized view, or a composite type was accepted and never deployed. It is now
/// deployed, changed in place, put back when a view is rebuilt for a dependency, scaffolded by <c>init</c>, and converges.
/// </summary>
[TestClass]
public sealed class RelationColumnCommentEndToEndTests
{
    private const string Before = """
        CREATE TABLE public.t (a int, b text);
        CREATE VIEW public.v AS SELECT a, b FROM public.t;
        COMMENT ON COLUMN public.v.a IS 'view a';
        CREATE MATERIALIZED VIEW public.mv AS SELECT a FROM public.t;
        COMMENT ON COLUMN public.mv.a IS 'mv a';
        CREATE TYPE public.ct AS (x int, y text);
        COMMENT ON COLUMN public.ct.x IS 'field x';
        """;

    // t.a is retyped, so v is dropped and created again around it (G-103) and must get its comments back.
    private const string After = """
        CREATE TABLE public.t (a bigint, b text);
        CREATE VIEW public.v AS SELECT a, b FROM public.t;
        COMMENT ON COLUMN public.v.a IS 'view a';
        COMMENT ON COLUMN public.v.b IS 'view b';
        CREATE MATERIALIZED VIEW public.mv AS SELECT a FROM public.t;
        COMMENT ON COLUMN public.mv.a IS 'mv a, changed';
        CREATE TYPE public.ct AS (x int, y text);
        COMMENT ON COLUMN public.ct.y IS 'field y';
        """;

    private const string CommentsQuery = """
        SELECT concat_ws(' | ',
            col_description('public.v'::regclass, 1), col_description('public.v'::regclass, 2), col_description('public.mv'::regclass, 1),
            coalesce((SELECT col_description(typrelid, 1) FROM pg_type WHERE typname = 'ct'), '-'), (SELECT col_description(typrelid, 2) FROM pg_type WHERE typname = 'ct'));
        """;

    [TestMethod]
    public async Task ColumnCommentsAreDeployedChangedRebuiltAndScaffolded()
    {
        var databaseName = await CreateDatabaseAsync("g216");
        var projectDirectory = CreateProjectDirectory("g216");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g216-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Before);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("view a | mv a | field x", await ScalarTextAsync(connectionString, CommentsQuery));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            WriteSchema(projectDirectory, After);
            var altered = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, altered.ExitCode, "the second publish failed.\n" + altered.Output);
            Assert.AreEqual("view a | view b | mv a, changed | - | field y", await ScalarTextAsync(connectionString, CommentsQuery));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            StringAssert.Contains(File.ReadAllText(Directory.GetFiles(scaffoldDirectory, "ct.sql", SearchOption.AllDirectories).Single()), "COMMENT ON COLUMN public.ct.y IS 'field y';");
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
