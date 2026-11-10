using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-340 against a real server: a default-privilege rule on types or functions with no <c>IN SCHEMA</c> makes PostgreSQL store the kind's whole list,
/// <c>PUBLIC</c>'s built-in <c>USAGE</c> or <c>EXECUTE</c> included, and pgproj read that entry as a rule the project did not state: <c>diff</c> reported it
/// after every publish, and <c>publish</c> planned <c>ALTER DEFAULT PRIVILEGES REVOKE ALL PRIVILEGES ON TYPES FROM PUBLIC</c>. It is PostgreSQL's default now.
/// </summary>
[TestClass]
public sealed class DefaultPrivilegePublicDefaultEndToEndTests
{
    private static string NewRole() => $"g340_{Guid.NewGuid():N}"[..20];

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AGlobalRuleOnTypesAndFunctionsConvergesAndLeavesPublicItsDefault(bool projectStatesPublic)
    {
        var role = NewRole();
        var other = NewRole();
        var databaseName = await CreateDatabaseAsync("g340");
        var projectDirectory = CreateProjectDirectory("g340");
        try
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE ROLE {role} NOLOGIN; CREATE ROLE {other} NOLOGIN;");
            var connectionString = ConnectionStringFor(databaseName);
            var rules = $"""
                ALTER DEFAULT PRIVILEGES GRANT USAGE ON TYPES TO {role};
                ALTER DEFAULT PRIVILEGES GRANT EXECUTE ON FUNCTIONS TO {role};

                """;
            if (projectStatesPublic)
            {
                rules += "ALTER DEFAULT PRIVILEGES GRANT USAGE ON TYPES TO PUBLIC;\nALTER DEFAULT PRIVILEGES GRANT EXECUTE ON FUNCTIONS TO PUBLIC;\n";
            }

            WriteSchema(projectDirectory, rules + """
                CREATE TYPE public.mood AS ENUM ('a');
                CREATE FUNCTION public.f() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;
                """);
            var projectFile = ProjectFile(projectDirectory);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.AreEqual("2", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_default_acl WHERE defaclnamespace = 0 AND defaclobjtype IN ('T', 'f')"),
                "PostgreSQL stores the global lists.");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.AreEqual(0, again.ExitCode, again.Output);
            Assert.IsFalse(again.Output.Contains("REVOKE", StringComparison.Ordinal), "Before G-340 the next publish revoked PUBLIC's default.\n" + again.Output);

            var executed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, executed.ExitCode, "Before G-340 this was refused as destructive.\n" + executed.Output);
            Assert.AreEqual("t t t t", await ScalarTextAsync(connectionString, $"""
                SELECT concat_ws(' ', has_type_privilege('{role}', 'public.mood', 'USAGE'), has_function_privilege('{role}', 'public.f()', 'EXECUTE'),
                                 has_type_privilege('{other}', 'public.mood', 'USAGE'), has_function_privilege('{other}', 'public.f()', 'EXECUTE'))
                """), "Every role keeps PUBLIC's default.");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP ROLE IF EXISTS {role}; DROP ROLE IF EXISTS {other};");
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task InitDoesNotWritePublicsDefaultAsARule()
    {
        var role = NewRole();
        var databaseName = await CreateDatabaseAsync("g340i");
        var projectDirectory = CreateProjectDirectory("g340i");
        try
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE ROLE {role} NOLOGIN;");
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, $"ALTER DEFAULT PRIVILEGES GRANT USAGE ON TYPES TO {role}; ALTER DEFAULT PRIVILEGES GRANT EXECUTE ON FUNCTIONS TO {role};");

            var scaffoldDirectory = Path.Combine(projectDirectory, "scaffold");
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var init = await RunAsync("init", "--source-connection", connectionString, "--output", scaffoldDirectory, "--project", scaffoldProject);
            Assert.AreEqual(0, init.ExitCode, init.Output);
            var sql = string.Concat(Directory.EnumerateFiles(scaffoldDirectory, "*.sql", SearchOption.AllDirectories).Select(File.ReadAllText));
            StringAssert.Contains(sql, $"ON TYPES TO {role}");
            StringAssert.Contains(sql, $"ON FUNCTIONS TO {role}");
            Assert.IsFalse(sql.Contains("ON TYPES TO PUBLIC", StringComparison.Ordinal) || sql.Contains("ON FUNCTIONS TO PUBLIC", StringComparison.Ordinal), sql);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP ROLE IF EXISTS {role};");
            TryDeleteDirectory(projectDirectory);
        }
    }
}
