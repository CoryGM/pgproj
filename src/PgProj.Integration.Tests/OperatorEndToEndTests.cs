using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-129 against a real server, on every version: operators deploy as written after the function and type they use, read back from <c>pg_operator</c>,
/// converge <c>0/0/0</c> (with PostgreSQL's two-way commutator and negator links, and a prefix operator with no left operand), are dropped and created again when
/// they change, round-trip through <c>init</c> into files of their own, and are not dropped by accident: removing one needs <c>--allow-destructive</c>, one
/// that belongs to an extension is left alone, a shell operator PostgreSQL makes for a partner that was never defined is not read as one, and a project that
/// lists <c>schemas</c> only manages the operators in those schemas.
/// </summary>
[TestClass]
public sealed class OperatorEndToEndTests
{
    private const string Schema = """
        CREATE TYPE public.mood AS ENUM ('happy', 'sad');
        CREATE FUNCTION public.mood_eq(mood, mood) RETURNS boolean LANGUAGE sql IMMUTABLE AS $$ SELECT $1::text = $2::text $$;
        CREATE FUNCTION public.mood_ne(mood, mood) RETURNS boolean LANGUAGE sql IMMUTABLE AS $$ SELECT $1::text <> $2::text $$;
        CREATE FUNCTION public.mood_is_happy(mood) RETURNS boolean LANGUAGE sql IMMUTABLE AS $$ SELECT $1::text = 'happy' $$;
        CREATE OPERATOR public.=== (LEFTARG = mood, RIGHTARG = mood, FUNCTION = public.mood_eq, COMMUTATOR = OPERATOR(public.===), NEGATOR = OPERATOR(public.!==), RESTRICT = eqsel, JOIN = eqjoinsel);
        CREATE OPERATOR public.!== (LEFTARG = mood, RIGHTARG = mood, FUNCTION = public.mood_ne, COMMUTATOR = OPERATOR(public.!==));
        CREATE OPERATOR public.@@@ (RIGHTARG = mood, FUNCTION = public.mood_is_happy);
        """;

    private const string Expected = "!==:mood:mood:!==:===:f:f|===:mood:mood:===:!==:f:f|@@@:-:mood:::f:f";

    [TestMethod]
    public async Task OperatorsDeployConvergeChangeRoundTripThroughInitAndAreNotDroppedByAccident()
    {
        var databaseName = await CreateDatabaseAsync("g129");
        var scaffoldDatabaseName = await CreateDatabaseAsync("g129s");
        var projectDirectory = CreateProjectDirectory("g129");
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
            Assert.AreEqual(Expected, await Operators(connectionString));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // The operators work, and the partner PostgreSQL links on its own is what the model expects.
            Assert.AreEqual("true", await ScalarTextAsync(connectionString, "SELECT ('happy'::public.mood === 'happy'::public.mood)::text;"));
            Assert.AreEqual("true", await ScalarTextAsync(connectionString, "SELECT ('happy'::public.mood !== 'sad'::public.mood)::text;"));
            Assert.AreEqual("true", await ScalarTextAsync(connectionString, "SELECT (@@@ 'happy'::public.mood)::text;"));

            // A second publish changes nothing.
            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, again.ExitCode, again.Output);

            // Round trip through init: each operator is in a file of its own, and the scaffold deploys the same into a second database.
            var scaffoldDirectory = Path.Combine(projectDirectory, "scaffold");
            var scaffolded = await RunAsync("init", "--source-connection", connectionString, "--output", scaffoldDirectory, "--project", Path.Combine(scaffoldDirectory, "pgproj.json"));
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            Assert.AreEqual(3, Directory.GetFiles(Path.Combine(scaffoldDirectory, "Schema", "public", "Operator"), "*.sql").Length);
            await AssertDiffIsEmptyAsync(Path.Combine(scaffoldDirectory, "pgproj.json"), connectionString);
            var scaffoldConnectionString = ConnectionStringFor(scaffoldDatabaseName);
            var deployedScaffold = await RunAsync("publish", "--project", Path.Combine(scaffoldDirectory, "pgproj.json"), "--target-connection", scaffoldConnectionString, "--execute");
            Assert.AreEqual(0, deployedScaffold.ExitCode, deployedScaffold.Output);
            Assert.AreEqual(Expected, await Operators(scaffoldConnectionString));
            await AssertDiffIsEmptyAsync(projectFile, scaffoldConnectionString);

            // A changed flag is a drop and a create, and is not destructive.
            WriteSchema(projectDirectory, Schema.Replace("JOIN = eqjoinsel", "JOIN = eqjoinsel, HASHES", StringComparison.Ordinal));
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            Assert.AreEqual(Expected.Replace("===:mood:mood:===:!==:f:f", "===:mood:mood:===:!==:t:f", StringComparison.Ordinal), await Operators(connectionString));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // Removing one is destructive.
            var withoutPrefix = Schema.ReplaceLineEndings("\n")
                .Replace("CREATE OPERATOR public.@@@ (RIGHTARG = mood, FUNCTION = public.mood_is_happy);", string.Empty, StringComparison.Ordinal)
                .Replace("JOIN = eqjoinsel", "JOIN = eqjoinsel, HASHES", StringComparison.Ordinal);
            WriteSchema(projectDirectory, withoutPrefix);
            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(1, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "operator dropped");
            Assert.AreEqual(1, Count(await Operators(connectionString), "@@@"), "Nothing was dropped.");

            var allowed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, allowed.ExitCode, allowed.Output);
            Assert.AreEqual(0, Count(await Operators(connectionString), "@@@"));
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
    public async Task AnOperatorThatAnExtensionOwnsIsLeftAlone()
    {
        var databaseName = await CreateDatabaseAsync("g129x");
        var projectDirectory = CreateProjectDirectory("g129x");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, "CREATE EXTENSION btree_gist;");
            Assert.AreNotEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_operator o WHERE EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_operator'::regclass AND d.objid = o.oid AND d.deptype = 'e');"));
            WriteSchema(projectDirectory, "CREATE EXTENSION btree_gist;\n");

            await AssertDiffIsEmptyAsync(ProjectFile(projectDirectory), connectionString);
            var published = await RunAsync("publish", "--project", ProjectFile(projectDirectory), "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_operator WHERE oid >= 16384 AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_operator'::regclass AND d.objid = pg_operator.oid AND d.deptype = 'e');"));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task AProjectThatListsSchemasOnlyManagesTheOperatorsInThem()
    {
        var databaseName = await CreateDatabaseAsync("g129sc");
        var projectDirectory = CreateProjectDirectory("g129sc", ", \"schemas\": [\"app\"]");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, "CREATE OPERATOR public.### (LEFTARG = integer, RIGHTARG = integer, FUNCTION = int4pl);");
            WriteSchema(projectDirectory, """
                CREATE SCHEMA app;
                CREATE OPERATOR app.### (LEFTARG = integer, RIGHTARG = integer, FUNCTION = int4mi);
                """);

            var published = await RunAsync("publish", "--project", ProjectFile(projectDirectory), "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("2", await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_operator WHERE oid >= 16384;"));
            await AssertDiffIsEmptyAsync(ProjectFile(projectDirectory), connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task AShellOperatorPostgreSqlMakesForAPartnerThatWasNeverDefinedIsNotAnOperator()
    {
        var databaseName = await CreateDatabaseAsync("g129h");
        var projectDirectory = CreateProjectDirectory("g129h");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            WriteSchema(projectDirectory, "CREATE OPERATOR public.<+> (LEFTARG = integer, RIGHTARG = integer, FUNCTION = int4pl, COMMUTATOR = OPERATOR(public.<->));\n");

            // The commutator that is not defined becomes a shell (an operator with no function); reading it as the project's would be a phantom operator.
            var published = await RunAsync("publish", "--project", ProjectFile(projectDirectory), "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("2", await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_operator WHERE oid >= 16384;"), "PostgreSQL made a shell for the commutator.");
            await AssertDiffIsEmptyAsync(ProjectFile(projectDirectory), connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    private static int Count(string operators, string name)
        => operators.Split('|', StringSplitOptions.RemoveEmptyEntries).Count(entry => entry.StartsWith(name + ":", StringComparison.Ordinal));

    private static Task<string> Operators(string connectionString)
        => ScalarTextAsync(
            connectionString,
            """
            SELECT string_agg(o.oprname || ':' || o.oprleft::regtype::text || ':' || o.oprright::regtype::text
                              || ':' || coalesce((SELECT c.oprname FROM pg_operator c WHERE c.oid = o.oprcom), '')
                              || ':' || coalesce((SELECT n.oprname FROM pg_operator n WHERE n.oid = o.oprnegate), '')
                              || ':' || (CASE WHEN o.oprcanhash THEN 't' ELSE 'f' END) || ':' || (CASE WHEN o.oprcanmerge THEN 't' ELSE 'f' END), '|' ORDER BY o.oprname)
            FROM pg_operator o
            WHERE o.oid >= 16384 AND o.oprcode::oid <> 0;
            """);
}
