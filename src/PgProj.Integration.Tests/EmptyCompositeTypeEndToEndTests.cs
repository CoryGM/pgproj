using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-268 against a real server. A composite type with no fields, <c>CREATE TYPE t AS ()</c>, was a TODO that <c>publish --execute</c> refused, and the live read dropped it, so
/// <c>init</c> left it out. It is deployed, read back, converges, takes its first fields in place, and round-trips through <c>init</c>.
/// </summary>
[TestClass]
public sealed class EmptyCompositeTypeEndToEndTests
{
    [TestMethod]
    public async Task ACompositeWithNoFieldsIsPublishedReadBackAndScaffolded()
    {
        var databaseName = await CreateDatabaseAsync("g268");
        var secondDatabase = await CreateDatabaseAsync("g268b");
        var projectDirectory = CreateProjectDirectory("g268");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g268-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, "CREATE TYPE public.empty AS ();\nCOMMENT ON TYPE public.empty IS 'nothing yet';\n");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_attribute WHERE attrelid = (SELECT typrelid FROM pg_type WHERE oid = 'public.empty'::regtype) AND attnum > 0"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // init scaffolds it, and the scaffold publishes into a second database and converges.
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var sql = string.Join("\n", Directory.GetFiles(Path.Combine(scaffoldDirectory, "sql"), "*.sql", SearchOption.AllDirectories).Select(File.ReadAllText));
            StringAssert.Contains(sql, "CREATE TYPE public.empty AS ()");
            var secondConnection = ConnectionStringFor(secondDatabase);
            var republished = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, republished.ExitCode, "publishing the scaffold failed.\n" + republished.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);

            // The first fields are added in place, keeping the type.
            var oid = await ScalarTextAsync(connectionString, "SELECT 'public.empty'::regtype::oid::text");
            WriteSchema(projectDirectory, "CREATE TYPE public.empty AS (a integer, b text);\nCOMMENT ON TYPE public.empty IS 'nothing yet';\n");
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            Assert.AreEqual(oid, await ScalarTextAsync(connectionString, "SELECT 'public.empty'::regtype::oid::text"));
            Assert.AreEqual("a,b", await ScalarTextAsync(connectionString, "SELECT string_agg(attname, ',' ORDER BY attnum) FROM pg_attribute WHERE attrelid = (SELECT typrelid FROM pg_type WHERE oid = 'public.empty'::regtype) AND attnum > 0"));
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
