using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-239 against a real server. An index key's operator class options lost their names, so <c>tsvector_ops (siglen = 1)</c> was deployed as
/// <c>tsvector_ops (1)</c> and publish failed. The index is now deployed, converges, and is scaffolded.
/// </summary>
[TestClass]
public sealed class OperatorClassOptionsEndToEndTests
{
    [TestMethod]
    public async Task AnIndexWithOperatorClassOptionsIsDeployedConvergesAndIsScaffolded()
    {
        var databaseName = await CreateDatabaseAsync("g239");
        var projectDirectory = CreateProjectDirectory("g239");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g239-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, """
                CREATE TABLE public.tv (a tsvector, b tsvector);
                CREATE INDEX tv_i ON public.tv USING gist (a tsvector_ops (siglen = 1), b tsvector_ops (siglen = 100));
                """);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
            StringAssert.Contains(await ScalarTextAsync(connectionString, "SELECT indexdef FROM pg_indexes WHERE indexname = 'tv_i'"), "siglen='1'");

            // The same index written another way is no change; a different value is.
            WriteSchema(projectDirectory, """
                CREATE TABLE public.tv (a tsvector, b tsvector);
                CREATE INDEX tv_i ON public.tv USING gist (a tsvector_ops (siglen='1'), b tsvector_ops (siglen = '100'));
                """);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
            WriteSchema(projectDirectory, """
                CREATE TABLE public.tv (a tsvector, b tsvector);
                CREATE INDEX tv_i ON public.tv USING gist (a tsvector_ops (siglen = 2), b tsvector_ops (siglen = 100));
                """);
            var changed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            StringAssert.Contains(changed.Output, "~ Index public.tv_i");

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
