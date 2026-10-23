using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-121 part 2 against a real server: PostgreSQL 18's temporal keys (<c>WITHOUT OVERLAPS</c> primary key and unique constraint, <c>PERIOD</c>
/// foreign key) deploy as written, read back from the catalog, converge <c>0/0/0</c>, reject an overlapping row, and round-trip through
/// <c>init</c>. On an older server <c>publish --execute</c> refuses, naming each temporal key.
/// </summary>
[TestClass]
public sealed class TemporalKeyEndToEndTests
{
    private const string Schema = """
        CREATE EXTENSION btree_gist;
        CREATE TABLE public.room (
            id integer NOT NULL,
            during tstzrange NOT NULL,
            label text,
            CONSTRAINT room_pk PRIMARY KEY (id, during WITHOUT OVERLAPS)
        );
        CREATE TABLE public.booking (
            id integer PRIMARY KEY,
            room_id integer NOT NULL,
            during tstzrange NOT NULL,
            CONSTRAINT booking_room_fk FOREIGN KEY (room_id, PERIOD during) REFERENCES public.room (id, PERIOD during)
        );
        CREATE TABLE public.plan (
            id integer NOT NULL,
            valid tstzrange NOT NULL,
            CONSTRAINT plan_uq UNIQUE (id, valid WITHOUT OVERLAPS)
        );
        """;

    [TestMethod]
    [PostgreSqlVersion(Minimum = 18)]
    public async Task TemporalKeysDeployConvergeAndRoundTripThroughInit()
    {
        var databaseName = await CreateDatabaseAsync("g121tk");
        var scaffoldDatabaseName = await CreateDatabaseAsync("g121tks");
        var projectDirectory = CreateProjectDirectory("g121tk");
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
            Assert.AreEqual("booking_room_fk:f:true|plan_uq:u:true|room_pk:p:true", await TemporalConstraints(connectionString));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // The database enforces them: overlapping rooms are refused, and a booking must be covered by the room's periods.
            await ExecuteAsync(connectionString, "INSERT INTO public.room VALUES (1, '[2026-01-01, 2026-06-01)', 'a'), (1, '[2026-06-01, 2026-12-31)', 'b');");
            await ExecuteAsync(connectionString, "INSERT INTO public.booking VALUES (1, 1, '[2026-05-01, 2026-07-01)');");
            var overlap = await Assert.ThrowsAsync<Npgsql.PostgresException>(
                () => ExecuteAsync(connectionString, "INSERT INTO public.room VALUES (1, '[2026-03-01, 2026-04-01)', 'c');"));
            Assert.AreEqual("23P01", overlap.SqlState);

            // A second publish changes nothing.
            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, again.ExitCode, again.Output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // Round trip through init: the scaffold says WITHOUT OVERLAPS and PERIOD, builds, and deploys the same into a second database.
            var scaffoldDirectory = Path.Combine(projectDirectory, "scaffold");
            var scaffolded = await RunAsync("init", "--source-connection", connectionString, "--output", scaffoldDirectory, "--project", Path.Combine(scaffoldDirectory, "pgproj.json"));
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var scaffoldedSql = string.Concat(Directory.EnumerateFiles(scaffoldDirectory, "*.sql", SearchOption.AllDirectories).Order().Select(File.ReadAllText));
            Assert.AreEqual(2, scaffoldedSql.Split("WITHOUT OVERLAPS").Length - 1, scaffoldedSql);
            Assert.AreEqual(2, scaffoldedSql.Split("PERIOD").Length - 1, scaffoldedSql);
            await AssertDiffIsEmptyAsync(Path.Combine(scaffoldDirectory, "pgproj.json"), connectionString);

            var scaffoldConnectionString = ConnectionStringFor(scaffoldDatabaseName);
            var deployedScaffold = await RunAsync("publish", "--project", Path.Combine(scaffoldDirectory, "pgproj.json"), "--target-connection", scaffoldConnectionString, "--execute");
            Assert.AreEqual(0, deployedScaffold.ExitCode, deployedScaffold.Output);
            Assert.AreEqual("booking_room_fk:f:true|plan_uq:u:true|room_pk:p:true", await TemporalConstraints(scaffoldConnectionString));
            await AssertDiffIsEmptyAsync(projectFile, scaffoldConnectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await DropDatabaseAsync(scaffoldDatabaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    [PostgreSqlVersion(Minimum = 18)]
    public async Task ChangingAKeyToAndFromTemporalDropsAndAddsIt()
    {
        var databaseName = await CreateDatabaseAsync("g121tkc");
        var projectDirectory = CreateProjectDirectory("g121tkc");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            const string Ordinary = "CREATE EXTENSION btree_gist;\nCREATE TABLE public.plan (id integer NOT NULL, valid tstzrange NOT NULL, CONSTRAINT plan_uq UNIQUE (id, valid));\n";
            WriteSchema(projectDirectory, Ordinary);
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);
            Assert.AreEqual("plan_uq:u:false", await TemporalConstraints(connectionString));

            WriteSchema(projectDirectory, Ordinary.Replace("valid)", "valid WITHOUT OVERLAPS)", StringComparison.Ordinal));
            var toTemporal = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, toTemporal.ExitCode, toTemporal.Output);
            Assert.AreEqual("plan_uq:u:true", await TemporalConstraints(connectionString));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            WriteSchema(projectDirectory, Ordinary);
            var back = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, back.ExitCode, back.Output);
            Assert.AreEqual("plan_uq:u:false", await TemporalConstraints(connectionString));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    [PostgreSqlVersion(Maximum = 17)]
    public async Task AnOlderServerRefusesToExecuteAProjectThatUsesTemporalKeysAndSaysWhy()
    {
        var databaseName = await CreateDatabaseAsync("g121tko");
        var projectDirectory = CreateProjectDirectory("g121tko");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            WriteSchema(projectDirectory, Schema);

            var executed = await RunAsync("publish", "--project", ProjectFile(projectDirectory), "--target-connection", connectionString, "--execute");
            Assert.AreEqual(1, executed.ExitCode, executed.Output);
            StringAssert.Contains(executed.Output, "temporal key (WITHOUT OVERLAPS) 'public.room.room_pk' (needs PostgreSQL 18)");
            StringAssert.Contains(executed.Output, "temporal key (WITHOUT OVERLAPS) 'public.plan.plan_uq' (needs PostgreSQL 18)");
            StringAssert.Contains(executed.Output, "temporal foreign key (PERIOD) 'public.booking.booking_room_fk' (needs PostgreSQL 18)");
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_tables WHERE schemaname = 'public';"), "Nothing was deployed.");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    private static Task<string> TemporalConstraints(string connectionString)
        => ScalarTextAsync(
            connectionString,
            "SELECT string_agg(conname || ':' || contype::text || ':' || conperiod, '|' ORDER BY conname) FROM pg_constraint WHERE connamespace = 'public'::regnamespace AND contype IN ('p', 'u', 'f') AND conname IN ('room_pk', 'plan_uq', 'booking_room_fk');");
}
