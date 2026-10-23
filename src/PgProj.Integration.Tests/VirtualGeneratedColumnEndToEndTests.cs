using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-122 and G-121 part 2 against a real server: PostgreSQL 18's virtual generated columns deploy as <c>VIRTUAL</c>, read back from
/// <c>attgenerated</c> as virtual (not stored, which G-122 fixed), converge <c>0/0/0</c>, compute their values, change between stored and virtual
/// by drop and add, and round-trip through <c>init</c>. On an older server <c>publish --execute</c> refuses, naming each virtual column.
/// </summary>
[TestClass]
public sealed class VirtualGeneratedColumnEndToEndTests
{
    private const string Schema = """
        CREATE TABLE public.g (
            a integer NOT NULL,
            stored_double integer GENERATED ALWAYS AS (a * 2) STORED,
            virtual_double integer GENERATED ALWAYS AS (a * 2) VIRTUAL,
            unspecified_triple integer GENERATED ALWAYS AS (a * 3)
        );
        """;

    [TestMethod]
    [PostgreSqlVersion(Minimum = 18)]
    public async Task VirtualColumnsDeployConvergeChangeAndRoundTripThroughInit()
    {
        var databaseName = await CreateDatabaseAsync("g122v");
        var scaffoldDatabaseName = await CreateDatabaseAsync("g122vs");
        var projectDirectory = CreateProjectDirectory("g122v");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Schema);

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            StringAssert.Contains(built.Output, "No diagnostics.");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("stored_double:s|unspecified_triple:v|virtual_double:v", await Kinds(connectionString));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // The values are computed by the server.
            await ExecuteAsync(connectionString, "INSERT INTO public.g (a) VALUES (7);");
            Assert.AreEqual("14|14|21", await ScalarTextAsync(connectionString, "SELECT stored_double || '|' || virtual_double || '|' || unspecified_triple FROM public.g;"));

            // A second publish changes nothing (and keeps the row).
            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, again.ExitCode, again.Output);
            Assert.AreEqual("1", await ScalarTextAsync(connectionString, "SELECT count(*) FROM public.g;"));

            // Round trip through init: the scaffold says VIRTUAL and STORED as the database has them, builds, and deploys the same.
            var scaffoldDirectory = Path.Combine(projectDirectory, "scaffold");
            var scaffolded = await RunAsync("init", "--source-connection", connectionString, "--output", scaffoldDirectory, "--project", Path.Combine(scaffoldDirectory, "pgproj.json"));
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var scaffoldedSql = string.Concat(Directory.EnumerateFiles(scaffoldDirectory, "*.sql", SearchOption.AllDirectories).Order().Select(File.ReadAllText));
            Assert.AreEqual(2, scaffoldedSql.Split(" VIRTUAL").Length - 1, scaffoldedSql);
            Assert.AreEqual(1, scaffoldedSql.Split(" STORED").Length - 1, scaffoldedSql);
            await AssertDiffIsEmptyAsync(Path.Combine(scaffoldDirectory, "pgproj.json"), connectionString);

            var scaffoldConnectionString = ConnectionStringFor(scaffoldDatabaseName);
            var deployedScaffold = await RunAsync("publish", "--project", Path.Combine(scaffoldDirectory, "pgproj.json"), "--target-connection", scaffoldConnectionString, "--execute");
            Assert.AreEqual(0, deployedScaffold.ExitCode, deployedScaffold.Output);
            Assert.AreEqual("stored_double:s|unspecified_triple:v|virtual_double:v", await Kinds(scaffoldConnectionString));
            await AssertDiffIsEmptyAsync(projectFile, scaffoldConnectionString);

            // Stored to virtual and back: each is dropped and added (a generated column loses nothing), and the row is still computed.
            WriteSchema(projectDirectory, Schema.Replace("(a * 2) STORED", "(a * 2) VIRTUAL", StringComparison.Ordinal).Replace("(a * 3)\n", "(a * 3) STORED\n", StringComparison.Ordinal));
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            Assert.AreEqual("stored_double:v|unspecified_triple:s|virtual_double:v", await Kinds(connectionString));
            Assert.AreEqual("14|14|21", await ScalarTextAsync(connectionString, "SELECT stored_double || '|' || virtual_double || '|' || unspecified_triple FROM public.g;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await DropDatabaseAsync(scaffoldDatabaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    [PostgreSqlVersion(Maximum = 17)]
    public async Task AnOlderServerRefusesToExecuteAProjectThatUsesVirtualColumnsAndSaysWhy()
    {
        var databaseName = await CreateDatabaseAsync("g122vo");
        var projectDirectory = CreateProjectDirectory("g122vo");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Schema);

            var executed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(1, executed.ExitCode, executed.Output);
            StringAssert.Contains(executed.Output, "VIRTUAL generated column 'public.g.virtual_double' (needs PostgreSQL 18)");
            StringAssert.Contains(executed.Output, "VIRTUAL generated column 'public.g.unspecified_triple' (needs PostgreSQL 18)");
            Assert.DoesNotContain("stored_double' (needs", executed.Output, StringComparison.Ordinal);
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_tables WHERE schemaname = 'public';"), "Nothing was deployed.");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    private static Task<string> Kinds(string connectionString)
        => ScalarTextAsync(
            connectionString,
            "SELECT string_agg(attname || ':' || attgenerated::text, '|' ORDER BY attname) FROM pg_attribute WHERE attrelid = 'public.g'::regclass AND attnum > 0 AND attgenerated <> '';");
}
