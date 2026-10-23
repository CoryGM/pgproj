using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-229 against a real server. An index whose predicate or expression named a column that another table also has was refused by build, so neither the project
/// nor init's scaffold of a database holding it could be published. It is now deployed, converges, and is scaffolded.
/// </summary>
[TestClass]
public sealed class IndexColumnBindingEndToEndTests
{
    [TestMethod]
    public async Task AnIndexOnAColumnAnotherTableAlsoHasIsDeployedAndScaffolded()
    {
        var databaseName = await CreateDatabaseAsync("g229");
        var projectDirectory = CreateProjectDirectory("g229");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g229-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, """
                CREATE TABLE public.other (d int, f1 int);
                CREATE TABLE public.t (b int, d int, f1 int) PARTITION BY RANGE (b);
                CREATE TABLE public.t_1 PARTITION OF public.t FOR VALUES FROM (1) TO (10);
                CREATE INDEX t_d_pred ON public.t USING btree (d) WHERE (d = 2);
                CREATE INDEX t_expr ON public.t ((d + f1));
                """);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var built = await RunAsync("build", "--project", scaffoldProject);
            Assert.AreEqual(0, built.ExitCode, built.Output);
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
