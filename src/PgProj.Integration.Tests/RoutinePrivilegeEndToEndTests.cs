using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-338 against a real server: a function or procedure that <c>publish</c> dropped and created came back with <c>PUBLIC</c>'s <c>EXECUTE</c> after it had been
/// revoked, silently, and a project could not state the revoke at all. Now <c>REVOKE ... ON FUNCTION|PROCEDURE ... FROM PUBLIC</c> is read, deployed, compared, and
/// put back on a recreated routine; <c>ALTER DEFAULT PRIVILEGES ... REVOKE ... FROM PUBLIC</c> is read and deployed (part (i)); <c>init</c> writes both; and a
/// default given back to an existing routine is named in a warning before anything runs (the user's decision of 2026-11-10).
/// </summary>
[TestClass]
public sealed class RoutinePrivilegeEndToEndTests
{
    private static string NewRole() => $"g338_{Guid.NewGuid():N}"[..20];

    private static async Task<string> CanExecuteAsync(string connectionString, string role, params string[] routines)
        => await ScalarTextAsync(connectionString, $"SELECT string_agg(has_function_privilege('{role}', r, 'EXECUTE')::text, ' ' ORDER BY ord) FROM unnest(ARRAY['{string.Join("', '", routines)}']) WITH ORDINALITY AS u(r, ord)");

    [TestMethod]
    public async Task ARoutineDroppedAndCreatedAgainKeepsItsRevoke()
    {
        // The case G-338 was found with: f is recreated (its return type changes), g replaced in place (its body changes).
        var role = NewRole();
        var databaseName = await CreateDatabaseAsync("g338");
        var projectDirectory = CreateProjectDirectory("g338");
        try
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE ROLE {role} NOLOGIN;");
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, """
                CREATE FUNCTION public.f() RETURNS integer LANGUAGE sql SECURITY DEFINER AS $$ SELECT 1 $$;
                REVOKE EXECUTE ON FUNCTION public.f() FROM PUBLIC;
                CREATE FUNCTION public.g() RETURNS integer LANGUAGE sql SECURITY DEFINER AS $$ SELECT 1 $$;
                REVOKE EXECUTE ON FUNCTION public.g() FROM PUBLIC;
                """);
            WriteSchema(projectDirectory, """
                CREATE FUNCTION public.f() RETURNS bigint LANGUAGE sql SECURITY DEFINER AS $$ SELECT 1 $$;
                REVOKE ALL ON FUNCTION public.f() FROM PUBLIC;
                CREATE FUNCTION public.g() RETURNS integer LANGUAGE sql SECURITY DEFINER AS $$ SELECT 2 $$;
                REVOKE ALL ON FUNCTION public.g() FROM PUBLIC;
                """);
            var projectFile = ProjectFile(projectDirectory);

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            Assert.IsFalse(built.Output.Contains("not modeled", StringComparison.Ordinal), "A routine's REVOKE ... FROM PUBLIC is modeled now.\n" + built.Output);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "Putting back what the routine had takes nothing away.\n" + published.Output);
            Assert.IsFalse(published.Output.Contains("WARNING: this publish gives PUBLIC", StringComparison.Ordinal), published.Output);
            Assert.AreEqual("false false", await CanExecuteAsync(connectionString, role, "public.f()", "public.g()"), "Before G-338, f came back executable by every role.");
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
    public async Task ARevokeTheProjectDoesNotStateIsGivenBackOnlyAfterAnExplicitWarningAndOneItAddsIsDestructive()
    {
        var role = NewRole();
        var databaseName = await CreateDatabaseAsync("g338w");
        var projectDirectory = CreateProjectDirectory("g338w");
        try
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE ROLE {role} NOLOGIN;");
            var connectionString = ConnectionStringFor(databaseName);
            const string routines = """
                CREATE FUNCTION public.f() RETURNS integer LANGUAGE sql SECURITY DEFINER AS $$ SELECT 1 $$;
                CREATE PROCEDURE public.p() LANGUAGE sql AS $$ SELECT 1 $$;

                """;
            await ExecuteAsync(connectionString, routines + "REVOKE ALL ON FUNCTION public.f() FROM PUBLIC; REVOKE ALL ON PROCEDURE public.p() FROM PUBLIC;");
            var projectFile = ProjectFile(projectDirectory);

            // The database revoked PUBLIC's EXECUTE and the project says nothing (a project from before G-338): diff now shows it, and publish names it.
            WriteSchema(projectDirectory, routines);
            var diff = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            StringAssert.Contains(diff.Output, "Removed: 2", diff.Output);
            var dryRun = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.AreEqual(0, dryRun.ExitCode, dryRun.Output);
            StringAssert.Contains(dryRun.Output, "WARNING: this publish gives PUBLIC privileges it does not have now. PUBLIC is every role, including roles created later");
            StringAssert.Contains(dryRun.Output, "function public.f() (SECURITY DEFINER: it runs with its owner's privileges, whoever calls it): every role can execute it.");
            StringAssert.Contains(dryRun.Output, "procedure public.p(): every role can execute it.");
            StringAssert.Contains(dryRun.Output, "REVOKE ALL PRIVILEGES ON FUNCTION \"public\".\"f\"() FROM PUBLIC;");
            StringAssert.Contains(dryRun.Output, "do not run this publish");
            Assert.AreEqual("false false", await CanExecuteAsync(connectionString, role, "public.f()", "public.p()"), "A dry run changes nothing.");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            StringAssert.Contains(published.Output, "WARNING: this publish gives PUBLIC privileges it does not have now.");
            Assert.AreEqual("true true", await CanExecuteAsync(connectionString, role, "public.f()", "public.p()"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // The project now revokes it from routines that exist: a revoke, refused without --allow-destructive.
            WriteSchema(projectDirectory, routines + "REVOKE ALL ON FUNCTION public.f() FROM PUBLIC;\nREVOKE ALL ON PROCEDURE public.p() FROM PUBLIC;\n");
            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreNotEqual(0, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "grant revoked: REVOKE ALL PRIVILEGES ON FUNCTION \"public\".\"f\"() FROM PUBLIC");
            Assert.AreEqual("true true", await CanExecuteAsync(connectionString, role, "public.f()", "public.p()"), "Nothing ran.");

            var allowed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, allowed.ExitCode, allowed.Output);
            Assert.AreEqual("false false", await CanExecuteAsync(connectionString, role, "public.f()", "public.p()"));
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
    public async Task ARuleCreatesRoutinesAndTypesWithoutTheDefaultAndConverges()
    {
        var role = NewRole();
        var databaseName = await CreateDatabaseAsync("g338r");
        var projectDirectory = CreateProjectDirectory("g338r");
        try
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE ROLE {role} NOLOGIN;");
            var connectionString = ConnectionStringFor(databaseName);
            const string older = "CREATE FUNCTION public.older() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;\n";
            await ExecuteAsync(connectionString, older);
            WriteSchema(projectDirectory, older + """
                ALTER DEFAULT PRIVILEGES REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
                ALTER DEFAULT PRIVILEGES REVOKE USAGE ON TYPES FROM PUBLIC;
                CREATE FUNCTION public.f() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;
                CREATE PROCEDURE public.p() LANGUAGE sql AS $$ SELECT 1 $$;
                CREATE TYPE public.mood AS ENUM ('a');
                """);
            var projectFile = ProjectFile(projectDirectory);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "A rule changes nothing that exists.\n" + published.Output);
            Assert.AreEqual("false false true", await CanExecuteAsync(connectionString, role, "public.f()", "public.p()", "public.older()"), "New routines are created without the default; an older one keeps it.");
            Assert.AreEqual("false", await ScalarTextAsync(connectionString, $"SELECT has_type_privilege('{role}', 'public.mood', 'USAGE')::text"));
            Assert.AreEqual("2", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_default_acl WHERE defaclnamespace = 0"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.AreEqual(0, again.ExitCode, again.Output);
            Assert.IsFalse(again.Output.Contains("PUBLIC", StringComparison.Ordinal), "A second publish changes nothing.\n" + again.Output);

            // The rule removed from the project: new objects get the default again, which is named, and nothing that exists changes.
            WriteSchema(projectDirectory, older + """
                ALTER DEFAULT PRIVILEGES REVOKE USAGE ON TYPES FROM PUBLIC;
                CREATE FUNCTION public.f() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;
                REVOKE ALL ON FUNCTION public.f() FROM PUBLIC;
                CREATE PROCEDURE public.p() LANGUAGE sql AS $$ SELECT 1 $$;
                REVOKE ALL ON PROCEDURE public.p() FROM PUBLIC;
                CREATE TYPE public.mood AS ENUM ('a');
                """);
            var removed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, removed.ExitCode, removed.Output);
            StringAssert.Contains(removed.Output, "every function and procedure created from now on: every role can execute it.");
            Assert.AreEqual("1", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_default_acl WHERE defaclnamespace = 0"));
            Assert.AreEqual("false false", await CanExecuteAsync(connectionString, role, "public.f()", "public.p()"));
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
    public async Task ARoutineRecreatedUnderAnotherRolesRuleKeepsTheRevokeTheDatabaseHad()
    {
        // The rule is another role's, so PostgreSQL does not apply it to a routine the deploying role creates: the revoke is put back from the database.
        var role = NewRole();
        var owner = NewRole();
        var databaseName = await CreateDatabaseAsync("g338o");
        var projectDirectory = CreateProjectDirectory("g338o");
        try
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE ROLE {role} NOLOGIN; CREATE ROLE {owner} NOLOGIN;");
            var connectionString = ConnectionStringFor(databaseName);
            var rule = $"ALTER DEFAULT PRIVILEGES FOR ROLE {owner} REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;\n";
            await ExecuteAsync(connectionString, rule + "CREATE FUNCTION public.f() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$; REVOKE ALL ON FUNCTION public.f() FROM PUBLIC;");
            WriteSchema(projectDirectory, rule + "CREATE FUNCTION public.f() RETURNS bigint LANGUAGE sql AS $$ SELECT 1 $$;\n");
            var projectFile = ProjectFile(projectDirectory);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.IsFalse(published.Output.Contains("WARNING: this publish gives PUBLIC", StringComparison.Ordinal), published.Output);
            Assert.AreEqual("false", await CanExecuteAsync(connectionString, role, "public.f()"), "The recreated routine keeps the revoke it had.");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP ROLE IF EXISTS {role}; DROP ROLE IF EXISTS {owner};");
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task InitWritesTheRevokesAndTheScaffoldBuildsTheSameInAnotherDatabase()
    {
        var role = NewRole();
        var databaseName = await CreateDatabaseAsync("g338i");
        var secondDatabase = await CreateDatabaseAsync("g338j");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g338-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE ROLE {role} NOLOGIN;");
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, $"""
                CREATE FUNCTION public.f(a integer, b text) RETURNS integer LANGUAGE sql SECURITY DEFINER AS $$ SELECT a $$;
                REVOKE ALL ON FUNCTION public.f(integer, text) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION public.f(integer, text) TO {role};
                CREATE PROCEDURE public.p() LANGUAGE sql AS $$ SELECT 1 $$;
                REVOKE ALL ON PROCEDURE public.p() FROM PUBLIC;
                CREATE FUNCTION public.open() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;
                ALTER DEFAULT PRIVILEGES REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
                """);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var fFile = File.ReadAllText(Directory.GetFiles(scaffoldDirectory, "f.sql", SearchOption.AllDirectories).Single());
            StringAssert.Contains(fFile, "REVOKE ALL ON FUNCTION public.f(integer, text) FROM PUBLIC;");
            var pFile = File.ReadAllText(Directory.GetFiles(scaffoldDirectory, "p.sql", SearchOption.AllDirectories).Single());
            StringAssert.Contains(pFile, "REVOKE ALL ON PROCEDURE public.p() FROM PUBLIC;");
            var openFile = File.ReadAllText(Directory.GetFiles(scaffoldDirectory, "open.sql", SearchOption.AllDirectories).Single());
            Assert.IsFalse(openFile.Contains("PUBLIC", StringComparison.Ordinal), "A routine with the default has nothing written.\n" + openFile);
            var all = string.Concat(Directory.EnumerateFiles(scaffoldDirectory, "*.sql", SearchOption.AllDirectories).Select(File.ReadAllText));
            StringAssert.Contains(all, "ALTER DEFAULT PRIVILEGES REVOKE ALL ON FUNCTIONS FROM PUBLIC;");
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            var secondConnection = ConnectionStringFor(secondDatabase);
            var copied = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, copied.ExitCode, copied.Output);
            // The rule is written before the routines, as running the scaffold's SQL on an empty database would, so open is created without the default here.
            Assert.AreEqual("true false false", await CanExecuteAsync(secondConnection, role, "public.f(integer, text)", "public.p()", "public.open()"), "Before G-338, every routine of a scaffold was PUBLIC's.");
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
