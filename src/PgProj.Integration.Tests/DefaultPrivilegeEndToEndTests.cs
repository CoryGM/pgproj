using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-339 against a real server: PostgreSQL gives a new object what its creator's default privileges (<c>ALTER DEFAULT PRIVILEGES ... GRANT</c>) name, and pgproj
/// expected a new object to have only the grants the project wrote for it, so the publish that created a table in a database with such a rule did not converge:
/// <c>diff</c> reported the rule's grant as one to remove, and the next publish planned its <c>REVOKE</c>. What a project's rule may give an object is now allowed
/// and not required (option (c), the user's decision of 2026-11-09), and the rules run before the objects they cover.
/// </summary>
[TestClass]
public sealed class DefaultPrivilegeEndToEndTests
{
    private static string NewRole() => $"g339_{Guid.NewGuid():N}"[..20];

    private static async Task<string> TableAclAsync(string connectionString, string table)
        => await ScalarTextAsync(connectionString, $"SELECT coalesce(relacl::text, 'null') FROM pg_class WHERE oid = '{table}'::regclass");

    private static async Task<bool> CanSelectAsync(string connectionString, string role, string table)
        => await ScalarTextAsync(connectionString, $"SELECT has_table_privilege('{role}', '{table}', 'SELECT')::text") == "true";

    [TestMethod]
    public async Task ATableCreatedUnderARuleTheDatabaseHasConverges()
    {
        var role = NewRole();
        var databaseName = await CreateDatabaseAsync("g339");
        var projectDirectory = CreateProjectDirectory("g339");
        try
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE ROLE {role} NOLOGIN;");
            var connectionString = ConnectionStringFor(databaseName);
            var rule = $"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO {role};";
            await ExecuteAsync(connectionString, rule);
            WriteSchema(projectDirectory, rule + "\nCREATE TABLE public.t (id integer PRIMARY KEY);\n");
            var projectFile = ProjectFile(projectDirectory);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.IsTrue(await CanSelectAsync(connectionString, role, "public.t"), "The server gives the new table what the rule names.");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.AreEqual(0, again.ExitCode, again.Output);
            Assert.IsFalse(again.Output.Contains("REVOKE", StringComparison.Ordinal), "Before G-339 the next publish revoked what the rule gave.\n" + again.Output);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP ROLE IF EXISTS {role};");
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task ARuleAndATableInOnePublishGiveTheTableWhatTheRuleNamesAndAnOlderTableIsLeftAlone()
    {
        var role = NewRole();
        var databaseName = await CreateDatabaseAsync("g339o");
        var projectDirectory = CreateProjectDirectory("g339o");
        try
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE ROLE {role} NOLOGIN;");
            var connectionString = ConnectionStringFor(databaseName);
            const string older = "CREATE TABLE public.older (id integer PRIMARY KEY);\n";
            await ExecuteAsync(connectionString, older);
            WriteSchema(projectDirectory, older + $"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO {role};\nCREATE TABLE public.t (id integer PRIMARY KEY);\n");
            var projectFile = ProjectFile(projectDirectory);

            var dryRun = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.AreEqual(0, dryRun.ExitCode, dryRun.Output);
            var ruleAt = dryRun.Output.IndexOf("ALTER DEFAULT PRIVILEGES", StringComparison.Ordinal);
            var tableAt = dryRun.Output.IndexOf("CREATE TABLE \"public\".\"t\"", StringComparison.Ordinal);
            Assert.IsTrue(ruleAt >= 0 && tableAt > ruleAt, "The rule runs before the table it covers.\n" + dryRun.Output);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.IsTrue(await CanSelectAsync(connectionString, role, "public.t"), await TableAclAsync(connectionString, "public.t"));
            Assert.IsFalse(await CanSelectAsync(connectionString, role, "public.older"), "A rule is not retroactive, and pgproj does not make it so.");
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
    public async Task ARuleRemovedFromTheProjectNoLongerCoversWhatItGave()
    {
        var role = NewRole();
        var databaseName = await CreateDatabaseAsync("g339r");
        var projectDirectory = CreateProjectDirectory("g339r");
        try
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE ROLE {role} NOLOGIN;");
            var connectionString = ConnectionStringFor(databaseName);
            const string table = "CREATE TABLE public.t (id integer PRIMARY KEY);\n";
            await ExecuteAsync(connectionString, $"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO {role};\n" + table);
            WriteSchema(projectDirectory, table);
            var projectFile = ProjectFile(projectDirectory);

            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreNotEqual(0, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "grant revoked");
            Assert.IsTrue(await CanSelectAsync(connectionString, role, "public.t"), "Nothing ran.");

            var allowed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, allowed.ExitCode, allowed.Output);
            Assert.IsFalse(await CanSelectAsync(connectionString, role, "public.t"));
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_default_acl"));
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
    public async Task EachKindARuleCoversConvergesUnderAGlobalAndAPerSchemaRule()
    {
        var role = NewRole();
        var databaseName = await CreateDatabaseAsync("g339k");
        var projectDirectory = CreateProjectDirectory("g339k");
        try
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE ROLE {role} NOLOGIN;");
            var connectionString = ConnectionStringFor(databaseName);
            var rules = $"""
                CREATE SCHEMA app;
                ALTER DEFAULT PRIVILEGES GRANT SELECT ON TABLES TO {role};
                ALTER DEFAULT PRIVILEGES GRANT USAGE ON TYPES TO {role};
                ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT USAGE ON TYPES TO {role};
                ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT EXECUTE ON FUNCTIONS TO {role};
                ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT USAGE ON SEQUENCES TO {role};
                """;
            await ExecuteAsync(connectionString, rules);
            WriteSchema(projectDirectory, rules + """

                CREATE TYPE app.mood AS ENUM ('a');
                CREATE DOMAIN app.pos AS integer;
                CREATE SEQUENCE app.q;
                CREATE TABLE app.t (id integer PRIMARY KEY);
                CREATE VIEW app.v AS SELECT id FROM app.t;
                CREATE FUNCTION app.f() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;
                CREATE PROCEDURE app.p() LANGUAGE sql AS $$ SELECT 1 $$;
                """);
            var projectFile = ProjectFile(projectDirectory);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("t t t t t t t", await ScalarTextAsync(connectionString, $"""
                SELECT concat_ws(' ', has_type_privilege('{role}', 'app.mood', 'USAGE'), has_type_privilege('{role}', 'app.pos', 'USAGE'),
                                 has_sequence_privilege('{role}', 'app.q', 'USAGE'), has_table_privilege('{role}', 'app.t', 'SELECT'),
                                 has_table_privilege('{role}', 'app.v', 'SELECT'), has_function_privilege('{role}', 'app.f()', 'EXECUTE'),
                                 has_function_privilege('{role}', 'app.p()', 'EXECUTE'))
                """));
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
