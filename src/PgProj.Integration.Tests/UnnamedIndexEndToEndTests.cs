using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-225 against a real server. An index written without a name was deployed under a name pgproj made up (<c>t_idx</c>), and a second one on the same table
/// refused <c>build</c>. Both are now deployed without a name, get the names PostgreSQL gives them, which are the model's, and converge, as does a scaffold.
/// </summary>
[TestClass]
public sealed class UnnamedIndexEndToEndTests
{
    [TestMethod]
    public async Task UnnamedIndexesGetPostgreSqlsNamesAndConverge()
    {
        var databaseName = await CreateDatabaseAsync("g225");
        var projectDirectory = CreateProjectDirectory("g225");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g225-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, """
                CREATE TABLE public.t (a int, b text);
                CREATE INDEX ON public.t (lower(b));
                CREATE INDEX ON public.t (a, b);
                CREATE INDEX ON public.t (a, b);
                CREATE UNIQUE INDEX ON public.t (b) INCLUDE (a) WHERE a > 0;
                """);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual(
                "t_a_b_idx,t_a_b_idx1,t_b_a_idx,t_lower_idx",
                await ScalarTextAsync(connectionString, "SELECT string_agg(c.relname, ',' ORDER BY c.relname) FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid WHERE i.indrelid = 'public.t'::regclass;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
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
