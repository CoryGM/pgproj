using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-192 against a real server. A composite field's <c>COLLATE "C"</c> was deployed as <c>COLLATE "c"</c>, and the publish failed with <c>42704</c>. The collation is now
/// deployed as written, read back, compared, scaffolded by <c>init</c>, and changed in place.
/// </summary>
[TestClass]
public sealed class CompositeFieldCollationEndToEndTests
{
    private const string Sql = """
        CREATE TYPE public.pt AS (x int, label text COLLATE "C", code text COLLATE "POSIX", plain text);
        CREATE TYPE public.loose AS (name text COLLATE "C");
        CREATE TABLE public.t (id int, p public.pt);
        CREATE TABLE public.typed OF public.pt; -- a typed table of fields written as float8 or varchar is G-199
        """;

    [TestMethod]
    public async Task FieldCollationsAreDeployedAndConverge_AndAChangeIsAppliedInPlace()
    {
        var databaseName = await CreateDatabaseAsync("g192");
        var projectDirectory = CreateProjectDirectory("g192");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g192-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual(
                "label:\"C\",code:\"POSIX\",plain:\"default\"",
                await ScalarTextAsync(connectionString, "SELECT string_agg(attname || ':' || attcollation::regcollation::text, ',' ORDER BY attnum) FROM pg_attribute WHERE attrelid = 'public.pt'::regclass AND attcollation <> 0;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            // A changed collation of a type no column uses is applied in place.
            WriteSchema(projectDirectory, Edit(Sql, "public.loose AS (name text COLLATE \"C\")", "public.loose AS (name text COLLATE \"POSIX\")"));
            var script = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            StringAssert.Contains(script.Output, "ALTER TYPE \"public\".\"loose\" ALTER ATTRIBUTE \"name\" TYPE text COLLATE \"POSIX\";");
            Assert.DoesNotContain("DROP TYPE", script.Output, StringComparison.Ordinal);
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, "publish --execute failed.\n" + changed.Output);
            Assert.AreEqual("\"POSIX\"", await ScalarTextAsync(connectionString, "SELECT attcollation::regcollation::text FROM pg_attribute WHERE attrelid = 'public.loose'::regclass AND attname = 'name';"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
