using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-121 part 2d against a real server: PostgreSQL 18's <c>NOT NULL ... NOT VALID</c> is added to a table that already has rows the constraint
/// would reject, read back as not valid, converges <c>0/0/0</c>, is validated by name when the project makes it a plain NOT NULL, is dropped and
/// added back when the project makes a valid one not valid, and round-trips through <c>init</c>. On an older server <c>publish --execute</c>
/// refuses, naming the column.
/// </summary>
[TestClass]
public sealed class NotNullNotValidEndToEndTests
{
    private const string NotValidProject = """
        CREATE TABLE public.t (id integer PRIMARY KEY, a integer, b integer);
        ALTER TABLE public.t ADD CONSTRAINT t_a_nn NOT NULL a NOT VALID;
        """;

    private const string ValidProject = """
        CREATE TABLE public.t (id integer PRIMARY KEY, a integer NOT NULL, b integer);
        """;

    [TestMethod]
    [PostgreSqlVersion(Minimum = 18)]
    public async Task ANotValidNotNullDeploysIsValidatedAndReturnsToNotValid()
    {
        var databaseName = await CreateDatabaseAsync("g121nn");
        var scaffoldDatabaseName = await CreateDatabaseAsync("g121nns");
        var projectDirectory = CreateProjectDirectory("g121nn");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);

            // A table with a row the NOT NULL would reject: NOT VALID leaves it alone, and enforces the constraint from now on.
            await ExecuteAsync(connectionString, "CREATE TABLE public.t (id integer PRIMARY KEY, a integer, b integer); INSERT INTO public.t VALUES (1, NULL, 1), (2, 5, 2);");
            WriteSchema(projectDirectory, NotValidProject);

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            StringAssert.Contains(built.Output, "No diagnostics.");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("t_a_nn:false", await NotNullConstraints(connectionString));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
            var refused = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(connectionString, "INSERT INTO public.t VALUES (3, NULL, 3);"));
            Assert.AreEqual("23502", refused.SqlState);
            Assert.AreEqual("1", await ScalarTextAsync(connectionString, "SELECT count(*) FROM public.t WHERE a IS NULL;"), "The existing row was left alone.");

            // A second publish changes nothing.
            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, again.ExitCode, again.Output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // Round trip through init: the scaffold has the NOT VALID constraint with its name, builds, and deploys the same.
            var scaffoldDirectory = Path.Combine(projectDirectory, "scaffold");
            var scaffolded = await RunAsync("init", "--source-connection", connectionString, "--output", scaffoldDirectory, "--project", Path.Combine(scaffoldDirectory, "pgproj.json"));
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var scaffoldedSql = string.Concat(Directory.EnumerateFiles(scaffoldDirectory, "*.sql", SearchOption.AllDirectories).Order().Select(File.ReadAllText));
            StringAssert.Contains(scaffoldedSql, "NOT NULL a NOT VALID");
            await AssertDiffIsEmptyAsync(Path.Combine(scaffoldDirectory, "pgproj.json"), connectionString);
            var scaffoldConnectionString = ConnectionStringFor(scaffoldDatabaseName);
            var deployedScaffold = await RunAsync("publish", "--project", Path.Combine(scaffoldDirectory, "pgproj.json"), "--target-connection", scaffoldConnectionString, "--execute");
            Assert.AreEqual(0, deployedScaffold.ExitCode, deployedScaffold.Output);
            Assert.AreEqual("t_a_nn:false", await NotNullConstraints(scaffoldConnectionString));
            await AssertDiffIsEmptyAsync(projectFile, scaffoldConnectionString);

            // Make it a plain NOT NULL: the existing row still breaks it, so the validation fails and rolls back...
            WriteSchema(projectDirectory, ValidProject);
            var failed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreNotEqual(0, failed.ExitCode, failed.Output);
            Assert.AreEqual("t_a_nn:false", await NotNullConstraints(connectionString));

            // ...and once the data is fixed, it is validated by name.
            await ExecuteAsync(connectionString, "UPDATE public.t SET a = 0 WHERE a IS NULL;");
            var validated = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, validated.ExitCode, validated.Output);
            Assert.AreEqual("t_a_nn:true", await NotNullConstraints(connectionString));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // Back to NOT VALID: the valid one is dropped and added back not valid.
            WriteSchema(projectDirectory, NotValidProject);
            var backToNotValid = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, backToNotValid.ExitCode, backToNotValid.Output);
            Assert.AreEqual("t_a_nn:false", await NotNullConstraints(connectionString));
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
    public async Task AnOlderServerRefusesToExecuteAProjectThatUsesNotValidNotNullAndSaysWhy()
    {
        var databaseName = await CreateDatabaseAsync("g121nno");
        var projectDirectory = CreateProjectDirectory("g121nno");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            WriteSchema(projectDirectory, NotValidProject);

            var executed = await RunAsync("publish", "--project", ProjectFile(projectDirectory), "--target-connection", connectionString, "--execute");
            Assert.AreEqual(1, executed.ExitCode, executed.Output);
            StringAssert.Contains(executed.Output, "NOT VALID NOT NULL on column 'public.t.a' (needs PostgreSQL 18)");
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_tables WHERE schemaname = 'public';"), "Nothing was deployed.");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    private static Task<string> NotNullConstraints(string connectionString)
        => ScalarTextAsync(
            connectionString,
            "SELECT string_agg(conname || ':' || convalidated::text, '|' ORDER BY conname) FROM pg_constraint WHERE conrelid = 'public.t'::regclass AND contype = 'n' AND conkey = ARRAY[(SELECT attnum FROM pg_attribute WHERE attrelid = 'public.t'::regclass AND attname = 'a')];");
}
