using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-151 against a real server. A tablespace is cluster-wide and needs a directory on the database server, so pgproj never creates one; a
/// table that names one the server lacks is refused by <c>publish --execute</c> before anything runs, instead of failing with <c>42704</c>
/// partway through, and a script that is only shown warns. A tablespace that exists is accepted and the project converges.
/// </summary>
[TestClass]
public sealed class TablespaceEndToEndTests
{
    [TestMethod]
    public async Task ATableNamingAMissingTablespaceIsRefusedBeforeAnythingRunsAndADryRunOnlyWarns()
    {
        var databaseName = await CreateDatabaseAsync("g151m");
        var projectDirectory = CreateProjectDirectory("g151m");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, "CREATE SCHEMA app;\nCREATE TABLE app.t (a integer) TABLESPACE g151_missing;\nCREATE TABLE app.u (a integer) TABLESPACE g151_missing;\n");

            var dryRun = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.AreEqual(0, dryRun.ExitCode, dryRun.Output);
            StringAssert.Contains(dryRun.Output, "Warning: the project's tables name tablespaces the server does not have, so this script cannot run on it:");
            StringAssert.Contains(dryRun.Output, "tablespace \"g151_missing\", named by app.t, app.u");
            // G-165 (deferred): a script that is only shown is not wrapped in a transaction, and the run says so.
            StringAssert.Contains(dryRun.Output, "Warning: the script is not wrapped in a transaction; run it inside one (psql --single-transaction -f <file>) or with pgproj publish --execute");

            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(1, refused.ExitCode, refused.Output);
            Assert.IsFalse(refused.Output.Contains("not wrapped in a transaction", StringComparison.Ordinal), "--execute runs in a transaction, so it does not warn.\n" + refused.Output);
            StringAssert.Contains(refused.Output, "Publish failed: the project's tables name tablespaces the server does not have, so the script was not run:");
            StringAssert.Contains(refused.Output, "tablespace \"g151_missing\", named by app.t, app.u");
            StringAssert.Contains(refused.Output, "pgproj does not create them");
            // The code as the server's error prints it ("42704: ..."); the bare digits can be in the temporary folder's name (G-301).
            Assert.IsFalse(refused.Output.Contains("42704:", StringComparison.Ordinal), "The server was never asked.\n" + refused.Output);
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*) FROM pg_namespace WHERE nspname = 'app';"), "Nothing was run.");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    /// <summary>
    /// G-153: PostgreSQL stores a table created in the database's default tablespace as having none of its own, whether or not the statement named it, so a table written
    /// <c>TABLESPACE pg_default</c> used to differ from the live one after every publish. In a database whose default is another tablespace, <c>pg_default</c> is a real,
    /// different one, and the default's own name is the one that reads as unset.
    /// </summary>
    [TestMethod]
    public async Task AnExplicitDefaultTablespaceConvergesInADatabaseWhosDefaultIsPgDefault()
    {
        var databaseName = await CreateDatabaseAsync("g153d");
        var projectDirectory = CreateProjectDirectory("g153d");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, "CREATE SCHEMA app;\nCREATE TABLE app.d (a integer) TABLESPACE pg_default;\nCREATE TABLE app.n (a integer);\n");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT reltablespace::text FROM pg_class WHERE oid = 'app.d'::regclass;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.IsFalse(again.Output.Contains("SET TABLESPACE", StringComparison.Ordinal), again.Output);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task ADatabaseWhoseDefaultIsAnotherTablespaceReadsItsOwnNameAsUnsetAndPgDefaultAsReal()
    {
        const string tablespace = "g153_ts";
        var databaseName = $"g153x_{Guid.NewGuid():N}";
        var projectDirectory = CreateProjectDirectory("g153x");
        try
        {
            await PostgresContainerFixture.CreateTablespaceDirectoryAsync("/tmp/g153_ts");
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE TABLESPACE {tablespace} LOCATION '/tmp/g153_ts';");
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE DATABASE {databaseName} TABLESPACE {tablespace};");

            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, $"CREATE SCHEMA app;\nCREATE TABLE app.in_default (a integer) TABLESPACE {tablespace};\nCREATE TABLE app.in_pg_default (a integer) TABLESPACE pg_default;\nCREATE TABLE app.plain (a integer);\n");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT reltablespace::text FROM pg_class WHERE oid = 'app.in_default'::regclass;"));
            Assert.AreEqual("pg_default", await ScalarTextAsync(connectionString, "SELECT spcname FROM pg_class c JOIN pg_tablespace s ON s.oid = c.reltablespace WHERE c.oid = 'app.in_pg_default'::regclass;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // init writes the one table that has a tablespace of its own and not the one in the default.
            var scaffoldDirectory = Path.Combine(projectDirectory, "scaffold");
            var scaffolded = await RunAsync("init", "--source-connection", connectionString, "--output", scaffoldDirectory, "--project", Path.Combine(scaffoldDirectory, "pgproj.json"));
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            await AssertDiffIsEmptyAsync(Path.Combine(scaffoldDirectory, "pgproj.json"), connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP TABLESPACE IF EXISTS {tablespace};");
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task ARealTablespaceIsAcceptedDeployedAndConverges()
    {
        const string tablespace = "g151_real";
        var databaseName = await CreateDatabaseAsync("g151r");
        var projectDirectory = CreateProjectDirectory("g151r");
        try
        {
            await PostgresContainerFixture.CreateTablespaceDirectoryAsync("/tmp/g151_real");
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE TABLESPACE {tablespace} LOCATION '/tmp/g151_real';");

            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, $"CREATE SCHEMA app;\nCREATE TABLE app.t (a integer) TABLESPACE {tablespace};\nCREATE TABLE app.n (a integer);\n");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.IsFalse(published.Output.Contains("tablespaces the server does not have", StringComparison.Ordinal), published.Output);
            Assert.AreEqual(tablespace, await ScalarTextAsync(connectionString, "SELECT spcname FROM pg_class c JOIN pg_tablespace s ON s.oid = c.reltablespace WHERE c.oid = 'app.t'::regclass;"));

            // The first real-tablespace convergence check: the table's tablespace reads back as the project wrote it.
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, again.ExitCode, again.Output);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP TABLESPACE IF EXISTS {tablespace};");
            TryDeleteDirectory(projectDirectory);
        }
    }
}
