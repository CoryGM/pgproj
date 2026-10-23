using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-217 against a real server. A view option written with its default value (<c>security_barrier = false</c>) was dropped from the deployed view, so
/// <c>reloptions</c> did not have it. It is now deployed as written, and still converges with the database.
/// </summary>
[TestClass]
public sealed class ViewOptionsAsWrittenEndToEndTests
{
    [TestMethod]
    public async Task AnOptionWrittenWithItsDefaultIsDeployedAndConverges()
    {
        var databaseName = await CreateDatabaseAsync("g217");
        var projectDirectory = CreateProjectDirectory("g217");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, "CREATE TABLE public.t (a int);\nCREATE VIEW public.v WITH (security_barrier = false) AS SELECT a FROM public.t;\n");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("security_barrier=false", await ScalarTextAsync(connectionString, "SELECT array_to_string(reloptions, ',') FROM pg_class WHERE oid = 'public.v'::regclass;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
