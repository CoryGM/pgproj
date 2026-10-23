using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-181 against a real server. <c>CREATE RECURSIVE VIEW</c> was scripted as a plain <c>CREATE OR REPLACE VIEW</c> without its column list, so
/// PostgreSQL refused it (<c>42P01</c>) and the whole publish rolled back.
/// </summary>
[TestClass]
public sealed class RecursiveViewEndToEndTests
{
    private const string Sql = """
        CREATE TABLE public.e (id int PRIMARY KEY, parent int, name text);
        CREATE RECURSIVE VIEW public.tree (id, depth) AS SELECT id, 0 FROM public.e WHERE parent IS NULL UNION ALL SELECT e.id, t.depth + 1 FROM public.e JOIN tree t ON e.parent = t.id;
        """;

    [TestMethod]
    public async Task ARecursiveViewIsDeployed_ConvergesIsChangedAndScaffolded()
    {
        var databaseName = await CreateDatabaseAsync("g181");
        var projectDirectory = CreateProjectDirectory("g181");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g181-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("id,depth", await ColumnsAsync(connectionString, "tree"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scriptPath = Path.Combine(projectDirectory, "second.sql");
            var republished = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--output", scriptPath);
            Assert.AreEqual(0, republished.ExitCode, republished.Output);
            Assert.DoesNotContain("CREATE OR REPLACE VIEW", File.Exists(scriptPath) ? await File.ReadAllTextAsync(scriptPath) : string.Empty, StringComparison.Ordinal);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            // A change to the recursive part is seen and deployed.
            WriteSchema(projectDirectory, Edit(Sql, "t.depth + 1", "t.depth + 2"));
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            StringAssert.Contains(await ScalarTextAsync(connectionString, "SELECT pg_get_viewdef('public.tree'::regclass)"), "depth + 2");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }

    private static Task<string> ColumnsAsync(string connectionString, string view)
        => ScalarTextAsync(connectionString, $"SELECT string_agg(column_name, ',' ORDER BY ordinal_position) FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{view}'");
}
