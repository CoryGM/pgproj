using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-196 against a real server. A domain's <c>COLLATE</c> was dropped: the domain was created with its base type's collation and no difference was reported. It is now
/// deployed, read back, compared, and scaffolded by <c>init</c>; a changed collation drops and creates a domain nothing uses, and is refused before anything runs while
/// a column uses the domain.
/// </summary>
[TestClass]
public sealed class DomainCollationEndToEndTests
{
    private const string Sql = """
        CREATE DOMAIN public.code AS text COLLATE "C" NOT NULL DEFAULT 'x' CHECK (length(VALUE) < 10);
        CREATE DOMAIN public.loose AS varchar(20) COLLATE "POSIX";
        CREATE DOMAIN public.plain AS text;
        CREATE TABLE public.t (id int, c public.code, p public.plain);
        """;

    [TestMethod]
    public async Task DomainCollationsAreDeployedAndConverge_AndAChangeIsAppliedOrRefused()
    {
        var databaseName = await CreateDatabaseAsync("g196");
        var projectDirectory = CreateProjectDirectory("g196");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g196-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual(
                "code:\"C\",loose:\"POSIX\",plain:\"default\"",
                await ScalarTextAsync(connectionString, "SELECT string_agg(typname || ':' || typcollation::regcollation::text, ',' ORDER BY typname) FROM pg_type WHERE typtype = 'd' AND typnamespace = 'public'::regnamespace;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            // A domain nothing uses is dropped and created with the new collation.
            WriteSchema(projectDirectory, Edit(Sql, "varchar(20) COLLATE \"POSIX\"", "varchar(20) COLLATE \"C\""));
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, "publish --execute failed.\n" + changed.Output);
            Assert.AreEqual("\"C\"", await ScalarTextAsync(connectionString, "SELECT typcollation::regcollation::text FROM pg_type WHERE oid = 'public.loose'::regtype;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A domain a column uses is refused before anything runs, and is left as it was.
            WriteSchema(projectDirectory, Edit(Edit(Sql, "varchar(20) COLLATE \"POSIX\"", "varchar(20) COLLATE \"C\""), "CREATE DOMAIN public.plain AS text;", "CREATE DOMAIN public.plain AS text COLLATE \"C\";"));
            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(1, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "public.plain");
            Assert.AreEqual("\"default\"", await ScalarTextAsync(connectionString, "SELECT typcollation::regcollation::text FROM pg_type WHERE oid = 'public.plain'::regtype;"));
            var dryRun = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            StringAssert.Contains(dryRun.Output, "-- TODO: The domain public.plain changes its collation");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
