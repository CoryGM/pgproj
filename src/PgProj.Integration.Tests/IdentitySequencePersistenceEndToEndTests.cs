using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-294 against a real server (PostgreSQL 15 and later, which accept <c>LOGGED</c> and <c>UNLOGGED</c> among an identity's options). An identity column's sequence follows its table's
/// persistence, and can be set the other way (<c>pg_dump</c> writes <c>UNLOGGED</c> inside the identity's options); that was dropped, so the sequence came back as its table's.
/// </summary>
[TestClass]
public sealed class IdentitySequencePersistenceEndToEndTests
{
    private const string Before = """
        CREATE TABLE public.t1 (a int GENERATED ALWAYS AS IDENTITY (UNLOGGED), b int);
        CREATE UNLOGGED TABLE public.t2 (a int GENERATED ALWAYS AS IDENTITY (LOGGED));
        CREATE UNLOGGED TABLE public.t3 (a int GENERATED ALWAYS AS IDENTITY);
        CREATE TABLE public.t4 (a int GENERATED ALWAYS AS IDENTITY);
        """;

    // t1's sequence becomes logged again, t4's unlogged; t2 and t3 are as they were.
    private const string After = """
        CREATE TABLE public.t1 (a int GENERATED ALWAYS AS IDENTITY (LOGGED), b int);
        CREATE UNLOGGED TABLE public.t2 (a int GENERATED ALWAYS AS IDENTITY (LOGGED));
        CREATE UNLOGGED TABLE public.t3 (a int GENERATED ALWAYS AS IDENTITY);
        CREATE TABLE public.t4 (a int GENERATED ALWAYS AS IDENTITY (UNLOGGED));
        """;

    private const string Query = """
        SELECT string_agg(c.relname || '=' || c.relpersistence::text, ' ' ORDER BY c.relname) FROM pg_class c WHERE c.relkind = 'S' AND c.relnamespace = 'public'::regnamespace;
        """;

    [TestMethod]
    [PostgreSqlVersion(Minimum = 15)]
    public async Task AnIdentitySequencesPersistenceIsDeployedChangedInPlaceAndScaffolded()
    {
        var databaseName = await CreateDatabaseAsync("g294");
        var scaffoldDatabaseName = await CreateDatabaseAsync("g294s");
        var projectDirectory = CreateProjectDirectory("g294");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Before);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("t1_a_seq=u t2_a_seq=p t3_a_seq=u t4_a_seq=p", await ScalarTextAsync(connectionString, Query));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            WriteSchema(projectDirectory, After);
            var altered = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, altered.ExitCode, "the second publish failed.\n" + altered.Output);
            Assert.AreEqual("t1_a_seq=p t2_a_seq=p t3_a_seq=u t4_a_seq=u", await ScalarTextAsync(connectionString, Query));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldDirectory = Path.Combine(projectDirectory, "scaffold");
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--source-connection", connectionString, "--output", scaffoldDirectory, "--project", scaffoldProject);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            var scaffoldConnectionString = ConnectionStringFor(scaffoldDatabaseName);
            var deployedScaffold = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", scaffoldConnectionString, "--execute");
            Assert.AreEqual(0, deployedScaffold.ExitCode, deployedScaffold.Output);
            Assert.AreEqual("t1_a_seq=p t2_a_seq=p t3_a_seq=u t4_a_seq=u", await ScalarTextAsync(scaffoldConnectionString, Query));
            await AssertDiffIsEmptyAsync(scaffoldProject, scaffoldConnectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await DropDatabaseAsync(scaffoldDatabaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
