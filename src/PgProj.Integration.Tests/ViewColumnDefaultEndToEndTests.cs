using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-291 against a real server. A default set on a view's column (<c>ALTER VIEW v ALTER COLUMN b SET DEFAULT 100</c>, which <c>pg_dump</c> writes as
/// <c>ALTER TABLE ONLY v ...</c>) was warned about and ignored, and the live read did not look at it. It is now deployed, changed and removed in place,
/// put back when the view is rebuilt, scaffolded by <c>init</c>, and converges.
/// </summary>
[TestClass]
public sealed class ViewColumnDefaultEndToEndTests
{
    private const string Before = """
        CREATE TABLE public.t (a int, b int, c text);
        CREATE VIEW public.v AS SELECT a, b, c FROM public.t;
        ALTER TABLE ONLY public.v ALTER COLUMN b SET DEFAULT 100;
        ALTER VIEW public.v ALTER COLUMN c SET DEFAULT 'x'::text;
        """;

    // b's default changes, c's goes; a gains one. t.a is also retyped, so v is dropped and created again around it (G-103) and must get its defaults back.
    private const string After = """
        CREATE TABLE public.t (a bigint, b int, c text);
        CREATE VIEW public.v AS SELECT a, b, c FROM public.t;
        ALTER VIEW public.v ALTER COLUMN a SET DEFAULT 1;
        ALTER VIEW public.v ALTER COLUMN b SET DEFAULT 200;
        """;

    private const string DefaultsQuery = """
        SELECT coalesce(string_agg(att.attname || '=' || pg_get_expr(d.adbin, d.adrelid), ' | ' ORDER BY att.attnum), '-')
        FROM pg_attrdef d JOIN pg_attribute att ON att.attrelid = d.adrelid AND att.attnum = d.adnum
        WHERE d.adrelid = 'public.v'::regclass;
        """;

    [TestMethod]
    public async Task AViewColumnDefaultIsDeployedChangedRemovedRebuiltAndScaffolded()
    {
        var databaseName = await CreateDatabaseAsync("g291");
        var secondDatabaseName = await CreateDatabaseAsync("g291b");
        var projectDirectory = CreateProjectDirectory("g291");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g291-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Before);

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            Assert.IsFalse(built.Output.Contains("does not declare", StringComparison.Ordinal) || built.Output.Contains("not modeled", StringComparison.Ordinal), "the defaults are modeled:\n" + built.Output);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("b=100 | c='x'::text", await ScalarTextAsync(connectionString, DefaultsQuery));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // The default really is used by an insert through the view.
            await ExecuteAsync(connectionString, "INSERT INTO public.v (a) VALUES (1);");
            Assert.AreEqual("100 | x", await ScalarTextAsync(connectionString, "SELECT b || ' | ' || c FROM public.t;"));
            await ExecuteAsync(connectionString, "DELETE FROM public.t;");

            // The same view with a default written another way is no change.
            WriteSchema(projectDirectory, Before.Replace("SET DEFAULT 100", "SET DEFAULT (100)").Replace("'x'::text", "'x'"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            WriteSchema(projectDirectory, After);
            var diff = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            StringAssert.Contains(diff.Output, "Column defaults");
            var altered = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, altered.ExitCode, "the second publish failed.\n" + altered.Output);
            Assert.AreEqual("a=1 | b=200", await ScalarTextAsync(connectionString, DefaultsQuery));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            StringAssert.Contains(File.ReadAllText(Directory.GetFiles(scaffoldDirectory, "v.sql", SearchOption.AllDirectories).Single()), "SET DEFAULT 200;");
            var scaffoldBuilt = await RunAsync("build", "--project", scaffoldProject);
            Assert.AreEqual(0, scaffoldBuilt.ExitCode, scaffoldBuilt.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            var secondConnection = ConnectionStringFor(secondDatabaseName);
            var scaffoldPublished = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, scaffoldPublished.ExitCode, scaffoldPublished.Output);
            Assert.AreEqual("a=1 | b=200", await ScalarTextAsync(secondConnection, DefaultsQuery));
            await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await DropDatabaseAsync(secondDatabaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
