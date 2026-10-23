using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-131 against a real server, on every version: casts deploy as written after the function and types they use, read back from <c>pg_cast</c>,
/// converge <c>0/0/0</c>, are dropped and created again when they change, round-trip through <c>init</c> into files of their own, and are not dropped by
/// accident: removing one needs <c>--allow-destructive</c>, one that belongs to an extension is left alone, and a project that lists <c>schemas</c> only
/// manages the casts that involve those schemas.
/// </summary>
[TestClass]
public sealed class CastEndToEndTests
{
    private const string Schema = """
        CREATE TYPE public.mood AS ENUM ('happy', 'sad');
        CREATE TYPE public.color AS ENUM ('red', 'blue');
        CREATE FUNCTION public.text_to_mood(text) RETURNS public.mood LANGUAGE sql IMMUTABLE AS $$ SELECT CASE $1 WHEN 'happy' THEN 'happy'::public.mood ELSE 'sad'::public.mood END $$;
        CREATE CAST (text AS public.mood) WITH FUNCTION public.text_to_mood(text) AS ASSIGNMENT;
        CREATE CAST (public.mood AS public.color) WITH INOUT AS IMPLICIT;
        """;

    private const string Expected = "mood>color:i:i|text>mood:a:f";

    [TestMethod]
    public async Task CastsDeployConvergeChangeRoundTripThroughInitAndAreNotDroppedByAccident()
    {
        var databaseName = await CreateDatabaseAsync("g131");
        var scaffoldDatabaseName = await CreateDatabaseAsync("g131s");
        var projectDirectory = CreateProjectDirectory("g131");
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
            Assert.AreEqual(Expected, await Casts(connectionString));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // The cast works: an assignment from text into the enum column goes through the function.
            await ExecuteAsync(connectionString, "CREATE TABLE public.person (id integer, feeling public.mood); INSERT INTO public.person VALUES (1, 'happy'::text);");
            Assert.AreEqual("happy", await ScalarTextAsync(connectionString, "SELECT feeling::text FROM public.person;"));
            await ExecuteAsync(connectionString, "DROP TABLE public.person;");

            // A second publish changes nothing.
            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, again.ExitCode, again.Output);

            // Round trip through init: each cast is in a file of its own, and the scaffold deploys the same into a second database.
            var scaffoldDirectory = Path.Combine(projectDirectory, "scaffold");
            var scaffolded = await RunAsync("init", "--source-connection", connectionString, "--output", scaffoldDirectory, "--project", Path.Combine(scaffoldDirectory, "pgproj.json"));
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            Assert.AreEqual(2, Directory.GetFiles(Path.Combine(scaffoldDirectory, "Cast"), "*.sql").Length);
            await AssertDiffIsEmptyAsync(Path.Combine(scaffoldDirectory, "pgproj.json"), connectionString);
            var scaffoldConnectionString = ConnectionStringFor(scaffoldDatabaseName);
            var deployedScaffold = await RunAsync("publish", "--project", Path.Combine(scaffoldDirectory, "pgproj.json"), "--target-connection", scaffoldConnectionString, "--execute");
            Assert.AreEqual(0, deployedScaffold.ExitCode, deployedScaffold.Output);
            Assert.AreEqual(Expected, await Casts(scaffoldConnectionString));
            await AssertDiffIsEmptyAsync(projectFile, scaffoldConnectionString);

            // A changed context is a drop and a create, and is not destructive.
            WriteSchema(projectDirectory, Schema.Replace("WITH FUNCTION public.text_to_mood(text) AS ASSIGNMENT", "WITH FUNCTION public.text_to_mood(text) AS IMPLICIT", StringComparison.Ordinal));
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            Assert.AreEqual("mood>color:i:i|text>mood:i:f", await Casts(connectionString));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // Removing one is destructive.
            WriteSchema(projectDirectory, Schema.ReplaceLineEndings("\n").Replace("CREATE CAST (public.mood AS public.color) WITH INOUT AS IMPLICIT;", string.Empty, StringComparison.Ordinal)
                .Replace("WITH FUNCTION public.text_to_mood(text) AS ASSIGNMENT", "WITH FUNCTION public.text_to_mood(text) AS IMPLICIT", StringComparison.Ordinal));
            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(1, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "cast dropped");
            Assert.AreEqual("mood>color:i:i|text>mood:i:f", await Casts(connectionString), "Nothing was dropped.");

            var allowed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, allowed.ExitCode, allowed.Output);
            Assert.AreEqual("text>mood:i:f", await Casts(connectionString));
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
    public async Task ACastThatAnExtensionOwnsIsLeftAlone()
    {
        var databaseName = await CreateDatabaseAsync("g131x");
        var projectDirectory = CreateProjectDirectory("g131x");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, """
                CREATE EXTENSION btree_gist;
                CREATE TYPE public.a AS ENUM ('x');
                CREATE TYPE public.b AS ENUM ('x');
                CREATE CAST (public.a AS public.b) WITH INOUT;
                ALTER EXTENSION btree_gist ADD TYPE public.a;
                ALTER EXTENSION btree_gist ADD TYPE public.b;
                ALTER EXTENSION btree_gist ADD CAST (public.a AS public.b);
                """);
            WriteSchema(projectDirectory, "CREATE EXTENSION btree_gist;\n");

            await AssertDiffIsEmptyAsync(ProjectFile(projectDirectory), connectionString);
            var published = await RunAsync("publish", "--project", ProjectFile(projectDirectory), "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("1", await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_cast WHERE oid >= 16384;"));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task AProjectThatListsSchemasOnlyManagesTheCastsThatInvolveThem()
    {
        var databaseName = await CreateDatabaseAsync("g131sc");
        var projectDirectory = CreateProjectDirectory("g131sc", ", \"schemas\": [\"app\"]");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, """
                CREATE TYPE public.other_a AS ENUM ('x');
                CREATE TYPE public.other_b AS ENUM ('x');
                CREATE CAST (public.other_a AS public.other_b) WITH INOUT;
                """);
            WriteSchema(projectDirectory, """
                CREATE SCHEMA app;
                CREATE TYPE app.a AS ENUM ('x');
                CREATE TYPE app.b AS ENUM ('x');
                CREATE CAST (app.a AS app.b) WITH INOUT;
                """);

            var published = await RunAsync("publish", "--project", ProjectFile(projectDirectory), "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("2", await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_cast WHERE oid >= 16384;"));
            await AssertDiffIsEmptyAsync(ProjectFile(projectDirectory), connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task TheCastsPostgreSqlMakesForARangeTypeAreNotUserDefinedCasts()
    {
        var databaseName = await CreateDatabaseAsync("g131r");
        var scaffoldDatabaseName = await CreateDatabaseAsync("g131rs");
        var projectDirectory = CreateProjectDirectory("g131r");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            WriteSchema(projectDirectory, "CREATE TYPE public.shade AS RANGE (subtype = integer);\n");

            // A range type gets a cast to its multirange type (deptype i, part of the type); reading it as the project's would give a phantom cast.
            var published = await RunAsync("publish", "--project", ProjectFile(projectDirectory), "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("1", await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_cast WHERE oid >= 16384;"), "PostgreSQL made its own cast for the range type.");
            await AssertDiffIsEmptyAsync(ProjectFile(projectDirectory), connectionString);

            var scaffoldDirectory = Path.Combine(projectDirectory, "scaffold");
            var scaffolded = await RunAsync("init", "--source-connection", connectionString, "--output", scaffoldDirectory, "--project", Path.Combine(scaffoldDirectory, "pgproj.json"));
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            Assert.IsFalse(Directory.Exists(Path.Combine(scaffoldDirectory, "Cast")), "init scaffolded a cast PostgreSQL made itself.");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await DropDatabaseAsync(scaffoldDatabaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    private static Task<string> Casts(string connectionString)
        => ScalarTextAsync(
            connectionString,
            "SELECT string_agg(format_type(c.castsource, null) || '>' || format_type(c.casttarget, null) || ':' || c.castcontext::text || ':' || c.castmethod::text, '|' ORDER BY c.castsource::regtype::text) FROM pg_cast c WHERE c.oid >= 16384 AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_cast'::regclass AND d.objid = c.oid AND d.deptype = 'i');");
}
