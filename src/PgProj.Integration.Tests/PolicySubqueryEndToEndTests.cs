using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-238 and G-245 against a real server. A policy whose <c>USING</c> or <c>WITH CHECK</c> is a subquery lost the subquery's parentheses, so publish failed with a syntax error (G-238),
/// and once it was deployed it never converged, because PostgreSQL prints a subquery differently from how it was written (G-245). It is now compared through PostgreSQL's own rendering:
/// deployed, nothing to do the second time, a real change found, and the project that <c>init</c> scaffolds from the database converges too.
/// </summary>
[TestClass]
public sealed class PolicySubqueryEndToEndTests
{
    private const string Tables = """
        CREATE TABLE public.m (c1 integer);
        CREATE TABLE public.t (c integer);
        ALTER TABLE public.t ENABLE ROW LEVEL SECURITY;
        """;

    private const string Policies = """
        CREATE POLICY p_check ON public.t FOR UPDATE USING (true) WITH CHECK ((SELECT t.c <= m.c1 FROM public.m));
        CREATE POLICY p_using ON public.t FOR SELECT USING (c IN (SELECT c1 FROM public.m));
        """;

    [TestMethod]
    public async Task APolicyWhoseClauseIsASubqueryIsDeployedConvergesAndIsScaffolded()
    {
        var databaseName = await CreateDatabaseAsync("g238");
        var projectDirectory = CreateProjectDirectory("g238");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g238-init-" + Guid.NewGuid().ToString("N"));
        var secondDatabase = await CreateDatabaseAsync("g238b");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Tables + Policies);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("2", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_policy WHERE polname IN ('p_check', 'p_using')"));

            // Converges (G-245): nothing to do, and the same policies written another way are the same policies.
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.IsFalse(again.Output.Contains("POLICY", StringComparison.Ordinal), "A second publish must change nothing.\n" + again.Output);
            WriteSchema(projectDirectory, Tables + """
                CREATE POLICY p_check ON public.t FOR UPDATE USING (true) WITH CHECK (( SELECT (t.c <= m.c1) FROM public.m ));
                CREATE POLICY p_using ON public.t FOR SELECT USING ((c IN ( SELECT m.c1 FROM public.m)));
                """);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A real change in the subquery is a change.
            WriteSchema(projectDirectory, Tables + """
                CREATE POLICY p_check ON public.t FOR UPDATE USING (true) WITH CHECK ((SELECT t.c < m.c1 FROM public.m));
                CREATE POLICY p_using ON public.t FOR SELECT USING (c IN (SELECT c1 FROM public.m));
                """);
            var changed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            StringAssert.Contains(changed.Output, "Policy public.t.p_check");
            Assert.IsFalse(changed.Output.Contains("Policy public.t.p_using", StringComparison.Ordinal), changed.Output);
            WriteSchema(projectDirectory, Tables + Policies);

            // init writes the subquery with its parentheses, so the scaffold builds, publishes into a second database, and converges there.
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var built = await RunAsync("build", "--project", scaffoldProject);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            var secondConnection = ConnectionStringFor(secondDatabase);
            var republished = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, republished.ExitCode, "publishing the scaffold failed.\n" + republished.Output);
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
}
