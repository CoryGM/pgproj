using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-130 against a real server, on every version: aggregates (an ordinary one with the state, final, combine, and moving-aggregate functions, an initial value, a sort
/// operator, and <c>PARALLEL</c>; a <c>(*)</c> one; an ordered-set one) deploy after the functions they use whatever order the file has, read back from
/// <c>pg_aggregate</c>, converge <c>0/0/0</c> although PostgreSQL fills in the options that were left out, behave, round-trip through <c>init</c>, are dropped and created
/// again when they change (with the view that calls one taken off first and put back), are rebuilt when a function under them is, and are not dropped by accident:
/// removing one needs <c>--allow-destructive</c>, and a project that lists <c>schemas</c> only manages the aggregates in those schemas.
/// </summary>
[TestClass]
public sealed class AggregateEndToEndTests
{
    private static string Schema(string parallel = "SAFE", string finalReturns = "bigint", string finalBody = "SELECT state::bigint")
        => $"""
            CREATE VIEW public.totals AS SELECT public.mysum(n) AS total, public.count_all(*) AS n FROM public.t;
            CREATE AGGREGATE public.mysum (integer) (SFUNC = public.sum_step, STYPE = integer, FINALFUNC = public.sum_final, COMBINEFUNC = public.sum_combine, INITCOND = '0', MSFUNC = public.sum_step, MINVFUNC = public.sum_inv, MSTYPE = integer, MFINALFUNC = public.sum_final, MINITCOND = '0', PARALLEL = {parallel}, SORTOP = >);
            CREATE AGGREGATE public.count_all (*) (SFUNC = int8inc, STYPE = bigint, INITCOND = '0');
            CREATE AGGREGATE public.myperc (float8 ORDER BY float8) (SFUNC = array_append, STYPE = float8[], FINALFUNC = public.perc_final);
            CREATE FUNCTION public.sum_step(state integer, x integer) RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT coalesce(state, 0) + x $$;
            CREATE FUNCTION public.sum_inv(state integer, x integer) RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT state - x $$;
            CREATE FUNCTION public.sum_combine(a integer, b integer) RETURNS integer LANGUAGE sql IMMUTABLE AS $$ SELECT a + b $$;
            CREATE FUNCTION public.sum_final(state integer) RETURNS {finalReturns} LANGUAGE sql IMMUTABLE AS $$ {finalBody} $$;
            CREATE FUNCTION public.perc_final(state float8[], fraction float8) RETURNS float8 LANGUAGE sql IMMUTABLE AS $$ SELECT state[ceil(array_length(state, 1) * fraction)::int] $$;
            CREATE TABLE public.t (id int PRIMARY KEY, n int NOT NULL);
            """;

    private const string Expected = "count_all:n:bigint|myperc:o:double precision[]|mysum:n:integer";

    [TestMethod]
    public async Task AggregatesDeployConvergeBehaveChangeRoundTripThroughInitAndAreNotDroppedByAccident()
    {
        var databaseName = await CreateDatabaseAsync("g130");
        var scaffoldDatabaseName = await CreateDatabaseAsync("g130s");
        var projectDirectory = CreateProjectDirectory("g130");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Schema());

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            StringAssert.Contains(built.Output, "No diagnostics.");

            // The view and the aggregates come before the functions and the table in the file, and still deploy after them.
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual(Expected, await Aggregates(connectionString));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // The aggregates work.
            await ExecuteAsync(connectionString, "INSERT INTO public.t VALUES (1, 10), (2, 20), (3, 30), (4, 40);");
            Assert.AreEqual("100|4|20", await ScalarTextAsync(connectionString, "SELECT (SELECT total FROM public.totals)::text || '|' || (SELECT n FROM public.totals)::text || '|' || (SELECT public.myperc(0.5) WITHIN GROUP (ORDER BY n) FROM public.t)::text;"));

            // A second publish changes nothing.
            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, again.ExitCode, again.Output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // Round trip through init: each aggregate is in a file of its own, and the scaffold deploys the same into a second database.
            var scaffoldDirectory = Path.Combine(projectDirectory, "scaffold");
            var scaffolded = await RunAsync("init", "--source-connection", connectionString, "--output", scaffoldDirectory, "--project", Path.Combine(scaffoldDirectory, "pgproj.json"));
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            Assert.AreEqual(3, Directory.GetFiles(Path.Combine(scaffoldDirectory, "Schema", "public", "Aggregate"), "*.sql").Length);
            await AssertDiffIsEmptyAsync(Path.Combine(scaffoldDirectory, "pgproj.json"), connectionString);
            var scaffoldConnectionString = ConnectionStringFor(scaffoldDatabaseName);
            var deployedScaffold = await RunAsync("publish", "--project", Path.Combine(scaffoldDirectory, "pgproj.json"), "--target-connection", scaffoldConnectionString, "--execute");
            Assert.AreEqual(0, deployedScaffold.ExitCode, deployedScaffold.Output);
            Assert.AreEqual(Expected, await Aggregates(scaffoldConnectionString));
            await AssertDiffIsEmptyAsync(projectFile, scaffoldConnectionString);

            // A changed option is a drop and a create, with the view that calls the aggregate taken off first and put back, and is not destructive.
            WriteSchema(projectDirectory, Schema(parallel: "RESTRICTED"));
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, "publish --execute failed.\n" + changed.Output);
            Assert.AreEqual("r", await ScalarTextAsync(connectionString, "SELECT proparallel::text FROM pg_proc WHERE proname = 'mysum';"));
            Assert.AreEqual("100", await ScalarTextAsync(connectionString, "SELECT total::text FROM public.totals;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // Removing one is destructive.
            var withoutPerc = Schema(parallel: "RESTRICTED").ReplaceLineEndings("\n")
                .Replace("CREATE AGGREGATE public.myperc (float8 ORDER BY float8) (SFUNC = array_append, STYPE = float8[], FINALFUNC = public.perc_final);\n", string.Empty, StringComparison.Ordinal);
            WriteSchema(projectDirectory, withoutPerc);
            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(1, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "aggregate dropped");
            StringAssert.Contains(await Aggregates(connectionString), "myperc");

            var allowed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, allowed.ExitCode, allowed.Output);
            Assert.AreEqual("count_all:n:bigint|mysum:n:integer", await Aggregates(connectionString));
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
    public async Task AFunctionUnderAnAggregateCanChangeItsReturnTypeAndTheAggregateAndItsViewComeBack()
    {
        var databaseName = await CreateDatabaseAsync("g130f");
        var projectDirectory = CreateProjectDirectory("g130f");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Schema());
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);
            await ExecuteAsync(connectionString, "INSERT INTO public.t VALUES (1, 10), (2, 20);");

            // sum_final returns integer now: DROP FUNCTION needs the aggregate, and the view that calls it, out of the way first.
            WriteSchema(projectDirectory, Schema(finalReturns: "integer", finalBody: "SELECT state"));
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, "publish --execute failed.\n" + changed.Output);
            Assert.AreEqual("integer|30", await ScalarTextAsync(connectionString, "SELECT pg_get_function_result('public.sum_final(int)'::regprocedure) || '|' || (SELECT total FROM public.totals)::text;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task AProjectThatListsSchemasOnlyManagesTheAggregatesInThem()
    {
        var databaseName = await CreateDatabaseAsync("g130sc");
        var projectDirectory = CreateProjectDirectory("g130sc", ", \"schemas\": [\"app\"]");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, "CREATE AGGREGATE public.other_count (*) (SFUNC = int8inc, STYPE = bigint, INITCOND = '0');");
            WriteSchema(projectDirectory, """
                CREATE SCHEMA app;
                CREATE AGGREGATE app.my_count (*) (SFUNC = int8inc, STYPE = bigint, INITCOND = '0');
                """);

            var published = await RunAsync("publish", "--project", ProjectFile(projectDirectory), "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("2", await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_aggregate a JOIN pg_proc p ON p.oid = a.aggfnoid WHERE p.oid >= 16384;"));
            await AssertDiffIsEmptyAsync(ProjectFile(projectDirectory), connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    private static Task<string> Aggregates(string connectionString)
        => ScalarTextAsync(
            connectionString,
            """
            SELECT string_agg(p.proname || ':' || a.aggkind::text || ':' || format_type(a.aggtranstype, null), '|' ORDER BY p.proname)
            FROM pg_aggregate a JOIN pg_proc p ON p.oid = a.aggfnoid
            WHERE p.oid >= 16384;
            """);
}
