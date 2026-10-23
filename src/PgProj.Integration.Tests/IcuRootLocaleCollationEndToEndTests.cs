using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-298 against a real server, on every version. An ICU collation on the root locale written with an empty locale (<c>locale = ''</c>, how <c>pg_dump</c> writes it before
/// PostgreSQL 16) was deployed with no locale at all, which 14 and 15 refuse. It is deployed as written, read back, and the same collation on 16 and later (<c>und</c>) is no change.
/// </summary>
[TestClass]
public sealed class IcuRootLocaleCollationEndToEndTests
{
    private const string Schema = """
        CREATE SCHEMA app;
        CREATE COLLATION app.case_sensitive (provider = icu, locale = '');
        CREATE COLLATION app.root_und (provider = icu, locale = 'und');
        """;

    private const string Query = """
        SELECT string_agg(c.collname || ':' || c.collprovider::text, '|' ORDER BY c.collname)
        FROM pg_collation c JOIN pg_namespace n ON n.oid = c.collnamespace WHERE n.nspname = 'app';
        """;

    [TestMethod]
    public async Task AnEmptyIcuLocaleIsDeployedAsWrittenConvergesAndRoundTripsThroughInit()
    {
        var databaseName = await CreateDatabaseAsync("g298");
        var scaffoldDatabaseName = await CreateDatabaseAsync("g298s");
        var projectDirectory = CreateProjectDirectory("g298");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Schema);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("case_sensitive:i|root_und:i", await ScalarTextAsync(connectionString, Query));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldDirectory = Path.Combine(projectDirectory, "scaffold");
            var scaffolded = await RunAsync("init", "--source-connection", connectionString, "--output", scaffoldDirectory, "--project", Path.Combine(scaffoldDirectory, "pgproj.json"));
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            var scaffoldConnectionString = ConnectionStringFor(scaffoldDatabaseName);
            var deployedScaffold = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", scaffoldConnectionString, "--execute");
            Assert.AreEqual(0, deployedScaffold.ExitCode, deployedScaffold.Output);
            Assert.AreEqual("case_sensitive:i|root_und:i", await ScalarTextAsync(scaffoldConnectionString, Query));
            await AssertDiffIsEmptyAsync(projectFile, scaffoldConnectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await DropDatabaseAsync(scaffoldDatabaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
