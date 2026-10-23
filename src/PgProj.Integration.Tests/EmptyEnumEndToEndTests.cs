using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-249 (and G-247) against a real server. An enum with no labels was emitted as a TODO that <c>publish --execute</c> refuses. It is deployed, read back, converges, takes its first labels in
/// place, and round-trips through <c>init</c>; a schema that holds nothing but its owner is created before the owner is set.
/// </summary>
[TestClass]
public sealed class EmptyEnumEndToEndTests
{
    [TestMethod]
    public async Task AnEnumWithNoLabelsAndASchemaWithOnlyAnOwnerArePublishedAndScaffolded()
    {
        var databaseName = await CreateDatabaseAsync("g249");
        var projectDirectory = CreateProjectDirectory("g249");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g249-init-" + Guid.NewGuid().ToString("N"));
        var secondDatabase = await CreateDatabaseAsync("g249b");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, """
                CREATE TYPE public.empty AS ENUM ();
                CREATE SCHEMA IF NOT EXISTS lonely;
                ALTER SCHEMA lonely OWNER TO postgres;
                """);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_enum WHERE enumtypid = 'public.empty'::regtype"));
            Assert.AreEqual("1", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_namespace WHERE nspname = 'lonely'"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var secondConnection = ConnectionStringFor(secondDatabase);
            var republished = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, republished.ExitCode, "publishing the scaffold failed.\n" + republished.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);

            // The first labels are added in place.
            WriteSchema(projectDirectory, """
                CREATE TYPE public.empty AS ENUM ('a', 'b');
                CREATE SCHEMA IF NOT EXISTS lonely;
                ALTER SCHEMA lonely OWNER TO postgres;
                """);
            var oid = await ScalarTextAsync(connectionString, "SELECT 'public.empty'::regtype::oid::text");
            await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual("a,b", await ScalarTextAsync(connectionString, "SELECT string_agg(enumlabel, ',' ORDER BY enumsortorder) FROM pg_enum WHERE enumtypid = 'public.empty'::regtype"));
            Assert.AreEqual(oid, await ScalarTextAsync(connectionString, "SELECT 'public.empty'::regtype::oid::text"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
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
