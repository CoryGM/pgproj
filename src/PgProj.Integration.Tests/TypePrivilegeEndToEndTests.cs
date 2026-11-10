using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-337 against a real server: privileges on enum, composite, and range types and on domains. A project grants <c>USAGE</c> with <c>GRANT ... ON TYPE|DOMAIN</c>
/// and takes away PostgreSQL's default (<c>USAGE</c> for <c>PUBLIC</c>) with <c>REVOKE ... FROM PUBLIC</c>, the one revoke pgproj reads (option (B), the user's
/// decision of 2026-11-09); a type without it is compared as having the default. Before, a grant on a type was a warning and ignored, the live read took no
/// <c>typacl</c>, and a type <c>publish</c> rebuilt came back with the default, silently.
/// </summary>
[TestClass]
public sealed class TypePrivilegeEndToEndTests
{
    private static string Roles(string suffix) => $"g337a_{suffix}";

    private static string ProjectSql(string role) => $"""
        CREATE TYPE public.mood AS ENUM ('a', 'b', 'c');
        CREATE DOMAIN public.pos AS integer CHECK (VALUE > 0);
        CREATE TYPE public.pair AS (x integer, y integer);
        CREATE TYPE public.fr AS RANGE (SUBTYPE = float8);
        CREATE TABLE public.t (id integer PRIMARY KEY, m public.mood, p public.pos, q public.pair);
        REVOKE ALL ON TYPE public.mood FROM PUBLIC;
        GRANT USAGE ON TYPE public.mood TO {role};
        REVOKE USAGE ON DOMAIN public.pos FROM PUBLIC;
        GRANT USAGE ON DOMAIN public.pos TO {role} WITH GRANT OPTION;
        GRANT ALL ON TYPE public.pair TO {role};
        """;

    private static async Task<string> PrivilegesAsync(string connectionString, string role)
        => await ScalarTextAsync(connectionString, $"""
            SELECT string_agg(format('%s:%s/%s%s', t, has_type_privilege('public', t, 'USAGE'), has_type_privilege('{role}', t, 'USAGE'),
                                     CASE WHEN has_type_privilege('{role}', t, 'USAGE WITH GRANT OPTION') THEN '+' ELSE '' END), ' ' ORDER BY t)
            FROM unnest(ARRAY['public.fr', 'public.mood', 'public.pair', 'public.pos']) AS t
            """);

    /// <summary>What <see cref="ProjectSql"/> gives: PUBLIC loses mood and pos, the role has all four (pos with grant option), fr keeps the default.</summary>
    private const string Expected = "public.fr:t/t public.mood:f/t public.pair:t/t public.pos:f/t+";

    [TestMethod]
    public async Task TypePrivilegesDeployConvergeAndRoundTripThroughInit()
    {
        var role = Roles(Guid.NewGuid().ToString("N")[..10]);
        var databaseName = await CreateDatabaseAsync("g337");
        var secondDatabase = await CreateDatabaseAsync("g337b");
        var projectDirectory = CreateProjectDirectory("g337");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g337-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE ROLE {role} NOLOGIN;");
            var connectionString = ConnectionStringFor(databaseName);
            WriteSchema(projectDirectory, ProjectSql(role));
            var projectFile = ProjectFile(projectDirectory);

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            Assert.IsFalse(built.Output.Contains("not modeled", StringComparison.Ordinal), "A grant or revoke on a type is modeled now.\n" + built.Output);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "A new type's revoke is not destructive.\n" + published.Output);
            Assert.IsFalse(published.Output.Contains("gives PUBLIC", StringComparison.Ordinal), "Nothing existed before, so nothing is widened.\n" + published.Output);
            Assert.AreEqual(Expected, await PrivilegesAsync(connectionString, role), published.Output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.AreEqual(0, again.ExitCode, again.Output);
            Assert.IsFalse(again.Output.Contains("ON TYPE", StringComparison.Ordinal) || again.Output.Contains("ON DOMAIN", StringComparison.Ordinal), "A second publish changes nothing.\n" + again.Output);

            // init writes each type's privileges in its own file, and the scaffold builds the same privileges in another database.
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var moodFile = File.ReadAllText(Directory.GetFiles(scaffoldDirectory, "mood.sql", SearchOption.AllDirectories).Single());
            StringAssert.Contains(moodFile, "REVOKE USAGE ON TYPE public.mood FROM PUBLIC;");
            StringAssert.Contains(moodFile, $"GRANT USAGE ON TYPE public.mood TO {role};");
            var posFile = File.ReadAllText(Directory.GetFiles(scaffoldDirectory, "pos.sql", SearchOption.AllDirectories).Single());
            StringAssert.Contains(posFile, "REVOKE USAGE ON DOMAIN public.pos FROM PUBLIC;");
            StringAssert.Contains(posFile, $"GRANT USAGE ON DOMAIN public.pos TO {role} WITH GRANT OPTION;");
            var frFile = File.ReadAllText(Directory.GetFiles(scaffoldDirectory, "fr.sql", SearchOption.AllDirectories).Single());
            Assert.IsFalse(frFile.Contains("PUBLIC", StringComparison.Ordinal), "A type with the default has nothing written.\n" + frFile);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            var secondConnection = ConnectionStringFor(secondDatabase);
            var copied = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, copied.ExitCode, copied.Output);
            Assert.AreEqual(Expected, await PrivilegesAsync(secondConnection, role));
            await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);
        }
        finally
        {
            await DropDatabaseAsync(secondDatabase);
            await DropDatabaseAsync(databaseName);
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP ROLE IF EXISTS {role};");
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }

    [TestMethod]
    public async Task ARevokedDefaultTheProjectDoesNotStateIsGivenBackWithAWarningAndARevokeItAddsIsDestructive()
    {
        var databaseName = await CreateDatabaseAsync("g337d");
        var projectDirectory = CreateProjectDirectory("g337d");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var plain = "CREATE TYPE public.mood AS ENUM ('a', 'b');\nCREATE TABLE public.t (id integer PRIMARY KEY, m public.mood);\n";
            await ExecuteAsync(connectionString, plain + "REVOKE USAGE ON TYPE public.mood FROM PUBLIC;");
            var projectFile = ProjectFile(projectDirectory);

            // The database revoked PUBLIC's USAGE and the project says nothing: the default is given back, which takes nothing away, and is named.
            WriteSchema(projectDirectory, plain);
            var diff = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            StringAssert.Contains(diff.Output, "Removed: 1", diff.Output);
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            StringAssert.Contains(published.Output, "gives PUBLIC (every role) privileges on objects the database already has");
            StringAssert.Contains(published.Output, "GRANT ALL PRIVILEGES ON TYPE \"public\".\"mood\" TO PUBLIC;");
            Assert.AreEqual("true", await ScalarTextAsync(connectionString, "SELECT has_type_privilege('public', 'public.mood', 'USAGE')::text"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // The project now revokes it from a type that exists: a revoke, refused without --allow-destructive.
            WriteSchema(projectDirectory, plain + "REVOKE USAGE ON TYPE public.mood FROM PUBLIC;");
            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreNotEqual(0, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "grant revoked: REVOKE ALL PRIVILEGES ON TYPE \"public\".\"mood\" FROM PUBLIC");
            Assert.AreEqual("true", await ScalarTextAsync(connectionString, "SELECT has_type_privilege('public', 'public.mood', 'USAGE')::text"), "Nothing ran.");

            var allowed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, allowed.ExitCode, allowed.Output);
            Assert.AreEqual("false", await ScalarTextAsync(connectionString, "SELECT has_type_privilege('public', 'public.mood', 'USAGE')::text"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task ATypeRebuiltUnderItsColumnsKeepsItsPrivileges()
    {
        var role = Roles(Guid.NewGuid().ToString("N")[..10]);
        var databaseName = await CreateDatabaseAsync("g337r");
        var projectDirectory = CreateProjectDirectory("g337r");
        try
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE ROLE {role} NOLOGIN;");
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, ProjectSql(role));
            var first = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, first.ExitCode, first.Output);
            await ExecuteAsync(connectionString, "INSERT INTO public.t VALUES (1, 'b', 5, ROW(1, 2));");

            // The enum's labels are reordered (G-191) and the composite's field is widened (G-119): both are rebuilt under the column, and neither is destructive.
            var changed = Edit(Edit(ProjectSql(role), "ENUM ('a', 'b', 'c')", "ENUM ('c', 'b', 'a')"), "AS (x integer, y integer)", "AS (x bigint, y integer)");
            WriteSchema(projectDirectory, changed);
            var rebuilt = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, rebuilt.ExitCode, rebuilt.Output);
            StringAssert.Contains(rebuilt.Output, "(enum public.mood rebuilt)");
            StringAssert.Contains(rebuilt.Output, "(composite public.pair rebuilt)");
            Assert.IsFalse(rebuilt.Output.Contains("gives PUBLIC", StringComparison.Ordinal), "Nothing is widened.\n" + rebuilt.Output);
            Assert.AreEqual(Expected, await PrivilegesAsync(connectionString, role), rebuilt.Output);
            Assert.AreEqual("b", await ScalarTextAsync(connectionString, "SELECT m::text FROM public.t"));
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
    public async Task WithGrantsExcludedARebuiltTypeKeepsWhatTheDatabaseHad()
    {
        var role = Roles(Guid.NewGuid().ToString("N")[..10]);
        var databaseName = await CreateDatabaseAsync("g337x");
        var projectDirectory = CreateProjectDirectory("g337x", ", \"excludeGrants\": true");
        try
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE ROLE {role} NOLOGIN;");
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, $"""
                CREATE TYPE public.mood AS ENUM ('a', 'b');
                CREATE TABLE public.t (id integer PRIMARY KEY, m public.mood);
                REVOKE USAGE ON TYPE public.mood FROM PUBLIC;
                GRANT USAGE ON TYPE public.mood TO {role};
                """);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, "CREATE TYPE public.mood AS ENUM ('b', 'a');\nCREATE TABLE public.t (id integer PRIMARY KEY, m public.mood);\n");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            StringAssert.Contains(published.Output, "(enum public.mood rebuilt)");
            Assert.AreEqual("false/true", await ScalarTextAsync(connectionString, $"SELECT has_type_privilege('public', 'public.mood', 'USAGE')::text || '/' || has_type_privilege('{role}', 'public.mood', 'USAGE')::text"), published.Output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP ROLE IF EXISTS {role};");
            TryDeleteDirectory(projectDirectory);
        }
    }
}
