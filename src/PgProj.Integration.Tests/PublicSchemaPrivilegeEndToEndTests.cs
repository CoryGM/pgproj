using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-341 against a real server: <c>PUBLIC</c>'s <c>USAGE</c> on the <c>public</c> schema was implicit, so a database that revoked it diffed as no change and a
/// project's <c>REVOKE USAGE ON SCHEMA public FROM PUBLIC</c> was a warning and ignored. Now it is read, deployed (destructive, as <c>public</c> always exists), compared,
/// and written by <c>init</c>; a default the project does not revoke is given back after the warning. <c>CREATE</c>, <c>PUBLIC</c>'s default there only before
/// PostgreSQL 15, is compared only when the project states it, so it is never given back (the user's decisions of 2026-11-10).
/// </summary>
[TestClass]
public sealed class PublicSchemaPrivilegeEndToEndTests
{
    private const string Table = "CREATE TABLE public.t (id integer);\n";

    private static string NewRole() => $"g341_{Guid.NewGuid():N}"[..20];

    private static async Task<string> PrivilegesAsync(string connectionString, string role)
        => await ScalarTextAsync(connectionString, $"SELECT has_schema_privilege('{role}', 'public', 'USAGE')::text || ' ' || has_schema_privilege('{role}', 'public', 'CREATE')::text");

    [TestMethod]
    public async Task ARevokeTheProjectStatesIsDestructiveThenDeployedAndConverges()
    {
        var role = NewRole();
        var databaseName = await CreateDatabaseAsync("g341");
        var projectDirectory = CreateProjectDirectory("g341");
        try
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE ROLE {role} NOLOGIN;");
            var connectionString = ConnectionStringFor(databaseName);
            var before = await PrivilegesAsync(connectionString, role);
            Assert.AreEqual(await ServerMajorAsync() < 15 ? "true true" : "true false", before, "The server's default for a new database.");

            WriteSchema(projectDirectory, Table + "REVOKE ALL ON SCHEMA public FROM PUBLIC;\n");
            var projectFile = ProjectFile(projectDirectory);

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            Assert.IsFalse(built.Output.Contains("not modeled", StringComparison.Ordinal), "REVOKE ... ON SCHEMA public FROM PUBLIC is modeled now.\n" + built.Output);

            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreNotEqual(0, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "grant revoked: REVOKE USAGE ON SCHEMA \"public\" FROM PUBLIC");
            Assert.AreEqual(await ServerMajorAsync() < 15, refused.Output.Contains("grant revoked: REVOKE CREATE ON SCHEMA \"public\" FROM PUBLIC", StringComparison.Ordinal),
                "CREATE is taken away only where PUBLIC has it (14).\n" + refused.Output);
            Assert.AreEqual(before, await PrivilegesAsync(connectionString, role), "Nothing ran.");

            var allowed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, allowed.ExitCode, allowed.Output);
            Assert.AreEqual("false false", await PrivilegesAsync(connectionString, role));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, again.ExitCode, "A second publish changes nothing, so nothing is refused.\n" + again.Output);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP ROLE IF EXISTS {role};");
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task UsageTheProjectDoesNotRevokeIsGivenBackAfterTheWarningAndCreateIsNot()
    {
        var role = NewRole();
        var databaseName = await CreateDatabaseAsync("g341w");
        var projectDirectory = CreateProjectDirectory("g341w");
        try
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE ROLE {role} NOLOGIN;");
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, Table + "REVOKE ALL ON SCHEMA public FROM PUBLIC;");
            WriteSchema(projectDirectory, Table);
            var projectFile = ProjectFile(projectDirectory);

            // Before G-341 this diffed as no change.
            var diff = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            StringAssert.Contains(diff.Output, "Removed: 1", diff.Output);

            var dryRun = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.AreEqual(0, dryRun.ExitCode, dryRun.Output);
            StringAssert.Contains(dryRun.Output, "WARNING: this publish gives PUBLIC privileges it does not have now. PUBLIC is every role, including roles created later");
            StringAssert.Contains(dryRun.Output, "schema public: every role can look up the objects in it, and use each one its own privileges allow.");
            StringAssert.Contains(dryRun.Output, "REVOKE USAGE ON SCHEMA \"public\" FROM PUBLIC;");
            Assert.IsFalse(dryRun.Output.Contains("GRANT CREATE", StringComparison.Ordinal), "CREATE is never given back.\n" + dryRun.Output);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("true false", await PrivilegesAsync(connectionString, role), "USAGE back; CREATE still revoked, on every version.");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP ROLE IF EXISTS {role};");
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task ADatabaseWithTheServersDefaultAndASilentProjectIsNoChange()
    {
        var databaseName = await CreateDatabaseAsync("g341d");
        var projectDirectory = CreateProjectDirectory("g341d");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, Table);
            WriteSchema(projectDirectory, Table);

            // On 15 and later the database reads as without PUBLIC's CREATE, which a project that does not state it does not compare; on 14 it has it.
            await AssertDiffIsEmptyAsync(ProjectFile(projectDirectory), connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task InitWritesTheRevokesAndTheScaffoldDeploysThemToAnotherDatabase()
    {
        var role = NewRole();
        var databaseName = await CreateDatabaseAsync("g341i");
        var secondDatabase = await CreateDatabaseAsync("g341j");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g341-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE ROLE {role} NOLOGIN;");
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, Table + "REVOKE ALL ON SCHEMA public FROM PUBLIC;");

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var schemaFile = File.ReadAllText(Directory.GetFiles(scaffoldDirectory, "public.sql", SearchOption.AllDirectories).Single(static path => path.Replace('\\', '/').EndsWith("Schema/public/public.sql", StringComparison.OrdinalIgnoreCase)));
            StringAssert.Contains(schemaFile, "REVOKE USAGE ON SCHEMA public FROM PUBLIC;");
            var onFourteen = await ServerMajorAsync() < 15;
            Assert.AreEqual(onFourteen, schemaFile.Contains("REVOKE CREATE ON SCHEMA public FROM PUBLIC;", StringComparison.Ordinal),
                "CREATE is written where it was revoked by someone (14), not where it is the server's default (15 and later).\n" + schemaFile);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            var secondConnection = ConnectionStringFor(secondDatabase);
            var refused = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreNotEqual(0, refused.ExitCode, "The public schema exists in a new database, so the revoke takes access away.\n" + refused.Output);
            var copied = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute", "--allow-destructive");
            Assert.AreEqual(0, copied.ExitCode, copied.Output);
            Assert.AreEqual("false false", await PrivilegesAsync(secondConnection, role), "Before G-341, a scaffold left the public schema open to every role.");
            await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);
        }
        finally
        {
            await DropDatabaseAsync(secondDatabase);
            await DropDatabaseAsync(databaseName);
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP ROLE IF EXISTS {role};");
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
