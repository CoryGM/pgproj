using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-121 part 2 against a real server: PostgreSQL 18's <c>NOT ENFORCED</c> CHECK and FOREIGN KEY constraints deploy as written, read back from
/// the catalog and converge <c>0/0/0</c>, change to and from enforced by dropping and adding the constraint, and round-trip through
/// <c>init</c>. On an older server <c>publish --execute</c> refuses, naming each constraint, before anything runs.
/// </summary>
[TestClass]
public sealed class NotEnforcedConstraintEndToEndTests
{
    private const string Schema = """
        CREATE TABLE public.parent (id integer PRIMARY KEY);
        CREATE TABLE public.child (
            id integer PRIMARY KEY,
            parent_id integer,
            qty integer CONSTRAINT qty_positive CHECK (qty > 0) NOT ENFORCED,
            CONSTRAINT child_parent_fk FOREIGN KEY (parent_id) REFERENCES public.parent (id) ON DELETE CASCADE NOT ENFORCED
        );
        """;

    [TestMethod]
    [PostgreSqlVersion(Minimum = 18)]
    public async Task NotEnforcedConstraintsDeployConvergeChangeAndRoundTripThroughInit()
    {
        var databaseName = await CreateDatabaseAsync("g121ne");
        var scaffoldDatabaseName = await CreateDatabaseAsync("g121nes");
        var projectDirectory = CreateProjectDirectory("g121ne");
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
            Assert.AreEqual("child_parent_fk:false|qty_positive:false", await Enforcement(connectionString));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // Not enforced means the server does not check: a row that breaks both is accepted.
            await ExecuteAsync(connectionString, "INSERT INTO public.child (id, parent_id, qty) VALUES (1, 999, -5);");

            // A second publish changes nothing.
            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, again.ExitCode, again.Output);
            Assert.AreEqual("child_parent_fk:false|qty_positive:false", await Enforcement(connectionString));

            // Round trip through init: the scaffold says NOT ENFORCED, builds, and deploys the same into a second database.
            var scaffoldDirectory = Path.Combine(projectDirectory, "scaffold");
            var scaffolded = await RunAsync("init", "--source-connection", connectionString, "--output", scaffoldDirectory, "--project", Path.Combine(scaffoldDirectory, "pgproj.json"));
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var scaffoldedSql = string.Concat(Directory.EnumerateFiles(scaffoldDirectory, "*.sql", SearchOption.AllDirectories).Order().Select(File.ReadAllText));
            Assert.AreEqual(2, scaffoldedSql.Split("NOT ENFORCED").Length - 1, scaffoldedSql);
            await AssertDiffIsEmptyAsync(Path.Combine(scaffoldDirectory, "pgproj.json"), connectionString);

            var scaffoldConnectionString = ConnectionStringFor(scaffoldDatabaseName);
            var deployedScaffold = await RunAsync("publish", "--project", Path.Combine(scaffoldDirectory, "pgproj.json"), "--target-connection", scaffoldConnectionString, "--execute");
            Assert.AreEqual(0, deployedScaffold.ExitCode, deployedScaffold.Output);
            Assert.AreEqual("child_parent_fk:false|qty_positive:false", await Enforcement(scaffoldConnectionString));
            await AssertDiffIsEmptyAsync(projectFile, scaffoldConnectionString);

            // Enforce the CHECK (the existing row breaks it, so first remove that row): the constraint is dropped and added back enforced.
            await ExecuteAsync(connectionString, "DELETE FROM public.child;");
            WriteSchema(projectDirectory, Schema.Replace("CHECK (qty > 0) NOT ENFORCED", "CHECK (qty > 0)", StringComparison.Ordinal));
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            Assert.AreEqual("child_parent_fk:false|qty_positive:true", await Enforcement(connectionString));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
            await AssertViolationRefusedAsync(connectionString);
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
    public async Task AnOlderServerRefusesToExecuteAProjectThatUsesNotEnforcedAndSaysWhy()
    {
        var databaseName = await CreateDatabaseAsync("g121nv");
        var projectDirectory = CreateProjectDirectory("g121nv");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Schema);

            var executed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(1, executed.ExitCode, executed.Output);
            StringAssert.Contains(executed.Output, "Publish failed: the project uses features the server (PostgreSQL 1");
            StringAssert.Contains(executed.Output, "NOT ENFORCED constraint 'public.child.child_parent_fk' (needs PostgreSQL 18)");
            StringAssert.Contains(executed.Output, "NOT ENFORCED constraint 'public.child.qty_positive' (needs PostgreSQL 18)");
            Assert.DoesNotContain("Executed deployment script", executed.Output, StringComparison.Ordinal);
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_tables WHERE schemaname = 'public';"), "Nothing was deployed.");

            // Only shown, the script may be for another server: it is printed with a warning.
            var dryRun = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString);
            Assert.AreEqual(0, dryRun.ExitCode, dryRun.Output);
            StringAssert.Contains(dryRun.Output, "Warning: the project uses features the server (PostgreSQL 1");
            StringAssert.Contains(dryRun.Output, "NOT ENFORCED");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    private static Task<string> Enforcement(string connectionString)
        => ScalarTextAsync(
            connectionString,
            "SELECT string_agg(conname || ':' || conenforced, '|' ORDER BY conname) FROM pg_constraint WHERE conrelid = 'public.child'::regclass AND contype IN ('c', 'f');");

    private static async Task AssertViolationRefusedAsync(string connectionString)
    {
        var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(
            () => ExecuteAsync(connectionString, "INSERT INTO public.child (id, parent_id, qty) VALUES (2, NULL, -1);"));
        Assert.AreEqual("23514", exception.SqlState);
    }
}
