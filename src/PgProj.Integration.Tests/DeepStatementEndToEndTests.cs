using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-272 against a real server. A file holding a statement nested deeper than 64 levels (a sum of about 23 terms) was dropped whole with a
/// warning, so `build` passed and `publish` deployed nothing from it; a few hundred levels overflowed the stack. A view, check, index, and
/// default a few hundred levels deep are now published with the rest of their file, converge, and round-trip through `init`.
/// </summary>
[TestClass]
public sealed class DeepStatementEndToEndTests
{
    private static string Sum(string term, int terms) => string.Join(" + ", Enumerable.Repeat(term, terms));

    [TestMethod]
    public async Task DeepStatementsArePublishedWithTheirFileConvergeAndAreScaffolded()
    {
        var databaseName = await CreateDatabaseAsync("g272");
        var projectDirectory = CreateProjectDirectory("g272");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g272-init-" + Guid.NewGuid().ToString("N"));
        var secondDatabase = await CreateDatabaseAsync("g272b");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, $"""
                CREATE TABLE public.t (
                    a integer,
                    b integer DEFAULT ({Sum("1", 300)}),
                    CONSTRAINT t_sum_check CHECK (({Sum("a", 300)}) > -1000000));
                CREATE INDEX t_sum_idx ON public.t (({Sum("a", 300)}));
                CREATE VIEW public.v_sum AS SELECT {Sum("a", 300)} AS total FROM public.t;
                CREATE VIEW public.v_nested AS {string.Concat(Enumerable.Repeat("SELECT * FROM (", 100))}SELECT a FROM public.t{string.Concat(Enumerable.Repeat(") s", 100))};
                CREATE TABLE public.after_them (id integer PRIMARY KEY);
                """);

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            Assert.DoesNotContain("parse tree", built.Output);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual(
                "after_them,t,t_sum_check,t_sum_idx,v_nested,v_sum",
                await ScalarTextAsync(connectionString, """
                    SELECT string_agg(name, ',' ORDER BY name) FROM (
                        SELECT relname AS name FROM pg_class WHERE relnamespace = 'public'::regnamespace AND relkind IN ('r', 'v', 'i') AND relname <> 'after_them_pkey'
                        UNION ALL SELECT conname FROM pg_constraint WHERE conrelid = 'public.t'::regclass AND contype = 'c') names
                    """));
            await ExecuteAsync(connectionString, "INSERT INTO public.t (a) VALUES (2)");
            Assert.AreEqual("600,300", await ScalarTextAsync(connectionString, "SELECT (SELECT total FROM public.v_sum)::text || ',' || (SELECT b FROM public.t)::text"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var republished = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.AreEqual(0, republished.ExitCode, republished.Output);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var secondConnection = ConnectionStringFor(secondDatabase);
            var scaffoldPublished = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, scaffoldPublished.ExitCode, "publishing the scaffold failed.\n" + scaffoldPublished.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);
            await AssertDiffIsEmptyAsync(projectFile, secondConnection);
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
