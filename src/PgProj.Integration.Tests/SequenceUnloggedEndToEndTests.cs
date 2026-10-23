using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-224 against a real server. <c>CREATE UNLOGGED SEQUENCE</c> was deployed as a logged sequence. It is now deployed unlogged, converges, is changed in place
/// either way, and is scaffolded; a serial's and an identity's sequence on an unlogged table follow the table and converge.
/// </summary>
[TestClass]
public sealed class SequenceUnloggedEndToEndTests
{
    [TestMethod]
    [PostgreSqlVersion(Minimum = 15)]
    public async Task AnUnloggedSequenceIsDeployedChangedInPlaceAndScaffolded()
    {
        var databaseName = await CreateDatabaseAsync("g224");
        var projectDirectory = CreateProjectDirectory("g224");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g224-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            const string Table = "CREATE UNLOGGED TABLE public.u (id serial, j int GENERATED ALWAYS AS IDENTITY);\n";
            WriteSchema(projectDirectory, "CREATE UNLOGGED SEQUENCE public.s;\nCREATE SEQUENCE public.l;\n" + Table);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("l:p,s:u,u_id_seq:u,u_j_seq:u", await ScalarTextAsync(connectionString, "SELECT string_agg(relname || ':' || relpersistence::text, ',' ORDER BY relname) FROM pg_class WHERE relkind = 'S' AND relnamespace = 'public'::regnamespace;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            await ExecuteAsync(connectionString, "SELECT nextval('public.s'), nextval('public.s');");
            WriteSchema(projectDirectory, "CREATE SEQUENCE public.s;\nCREATE UNLOGGED SEQUENCE public.l;\n" + Table);
            var altered = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, altered.ExitCode, "the second publish failed.\n" + altered.Output);
            Assert.AreEqual("l:u,s:p", await ScalarTextAsync(connectionString, "SELECT string_agg(relname || ':' || relpersistence::text, ',' ORDER BY relname) FROM pg_class WHERE relname IN ('s', 'l') AND relnamespace = 'public'::regnamespace;"));
            Assert.AreEqual("3", await ScalarTextAsync(connectionString, "SELECT nextval('public.s')::text;"), "The counter is kept.");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            StringAssert.Contains(File.ReadAllText(Directory.GetFiles(scaffoldDirectory, "l.sql", SearchOption.AllDirectories).Single()), "CREATE UNLOGGED SEQUENCE");
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
