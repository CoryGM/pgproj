using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-258 against a real server: a publication written the way <c>pg_dump</c> writes it (an empty <c>CREATE PUBLICATION</c> and one <c>ALTER PUBLICATION ... ADD TABLE</c> per table, with a
/// column list or a filter) is published with its tables, converges, and survives <c>init</c>.
/// </summary>
[TestClass]
public sealed class AlterPublicationEndToEndTests
{
    private const string Objects = """
        CREATE TABLE public.t (a integer, b integer, c integer);
        CREATE TABLE public.u (id integer PRIMARY KEY);
        CREATE SCHEMA s;
        CREATE TABLE s.w (x integer);
        CREATE PUBLICATION pub WITH (publish = 'insert, update');
        ALTER PUBLICATION pub ADD TABLE ONLY public.t (a, b) WHERE ((a > 0));
        ALTER PUBLICATION pub ADD TABLE ONLY public.u;
        CREATE PUBLICATION pub2;
        ALTER PUBLICATION pub2 ADD TABLE public.t;
        ALTER PUBLICATION pub2 ADD TABLE public.u;
        ALTER PUBLICATION pub2 DROP TABLE public.u;
        ALTER PUBLICATION pub2 SET (publish = 'delete');
        CREATE PUBLICATION pub3;
        ALTER PUBLICATION pub3 ADD TABLES IN SCHEMA s;
        """;

    private const string Query = "SELECT string_agg(p.pubname || ':' || p.pubinsert::text || p.pubupdate::text || p.pubdelete::text || ':' || coalesce((SELECT string_agg(r.prrelid::regclass::text || coalesce('(' || (SELECT string_agg(attname, ',') FROM pg_attribute WHERE attrelid = r.prrelid AND attnum = ANY (r.prattrs)) || ')', ''), ';' ORDER BY r.prrelid::regclass::text) FROM pg_publication_rel r WHERE r.prpubid = p.oid), '') || ':' || coalesce((SELECT string_agg(pn.pnnspid::regnamespace::text, ',') FROM pg_publication_namespace pn WHERE pn.pnpubid = p.oid), ''), '|' ORDER BY p.pubname) FROM pg_publication p";

    [TestMethod]
    [PostgreSqlVersion(Minimum = 15)]
    public async Task APublicationWrittenWithAlterPublicationPublishesConvergesAndRoundTrips()
    {
        var databaseName = await CreateDatabaseAsync("g258");
        var projectDirectory = CreateProjectDirectory("g258");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g258-init-" + Guid.NewGuid().ToString("N"));
        var secondDatabase = await CreateDatabaseAsync("g258b");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            const string expected = "pub:truetruefalse:t(a,b);u:|pub2:falsefalsetrue:t:|pub3:truetruetrue::s";

            WriteSchema(projectDirectory, Objects);
            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            Assert.IsFalse(built.Output.Contains("not modeled", StringComparison.Ordinal), built.Output);
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual(expected, await ScalarTextAsync(connectionString, Query));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var secondConnection = ConnectionStringFor(secondDatabase);
            var republished = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, republished.ExitCode, "publishing the scaffold failed.\n" + republished.Output);
            Assert.AreEqual(expected, await ScalarTextAsync(secondConnection, Query));
            await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);
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
