using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-132 against a real server (the user's design of 2026-11-10): foreign servers and user mappings on <c>postgres_fdw</c>, and a wrapper of the project's own, are
/// deployed, read back, compared, and scaffolded. A mapping's password is a variable, sent on create and with <c>--apply-secrets</c>, never read back or printed; a
/// server's option given by a variable is compared only by whether it is there; dropping a server or a mapping needs <c>--allow-destructive</c>; a wrapper needs
/// <c>--allow-superuser-statements</c>; and <c>init</c> writes the password as a variable and the scaffold builds the same in another database.
/// </summary>
[TestClass]
public sealed class ForeignDataEndToEndTests
{
    private static string NewName(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..20];

    private static string Project(string role, string passwordVariable, string hostVariable) => $"""
        CREATE EXTENSION postgres_fdw;
        CREATE SERVER remote FOREIGN DATA WRAPPER postgres_fdw OPTIONS (host :'{hostVariable}', dbname 'app', port '5432');
        COMMENT ON SERVER remote IS 'the remote database';
        GRANT USAGE ON FOREIGN SERVER remote TO {role};
        CREATE USER MAPPING FOR {role} SERVER remote OPTIONS (user 'remote_user', password :'{passwordVariable}');
        CREATE USER MAPPING FOR PUBLIC SERVER remote OPTIONS (user 'anonymous');

        """;

    private static async Task<string> OptionsAsync(string connectionString, string role)
        => await ScalarTextAsync(connectionString, $"SELECT array_to_string(umoptions, ',') FROM pg_user_mappings WHERE srvname = 'remote' AND usename = '{role}'");

    [TestMethod]
    public async Task AServerAndMappingsPublishWithTheirSecretsConvergeAndChangeInPlace()
    {
        var role = NewName("g132r");
        var password = NewName("pw");
        var host = NewName("host");
        var databaseName = await CreateDatabaseAsync("g132");
        var projectDirectory = CreateProjectDirectory("g132");
        try
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE ROLE {role} NOLOGIN;");
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Project(role, password, host));

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            Assert.IsFalse(built.Output.Contains("not modeled", StringComparison.Ordinal), built.Output);

            var dryRun = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            StringAssert.Contains(dryRun.Output, $"password :'{password}'", "A kept script keeps the variable.");

            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreNotEqual(0, refused.ExitCode, "No value for the variables: nothing runs.\n" + refused.Output);

            Environment.SetEnvironmentVariable($"PGPROJ_VAR_{password}", "first-secret");
            Environment.SetEnvironmentVariable($"PGPROJ_VAR_{host}", "db.example");
            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.IsFalse(published.Output.Contains("first-secret", StringComparison.Ordinal), published.Output);
            Assert.AreEqual("user=remote_user,password=first-secret", await OptionsAsync(connectionString, role));
            Assert.AreEqual("host=db.example,dbname=app,port=5432 the remote database", await ScalarTextAsync(connectionString, "SELECT array_to_string(srvoptions, ',') || ' ' || obj_description(oid, 'pg_foreign_server') FROM pg_foreign_server WHERE srvname = 'remote'"));
            Assert.AreEqual("true", await ScalarTextAsync(connectionString, $"SELECT has_server_privilege('{role}', 'remote', 'USAGE')::text"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // The host's value and the password are not compared: another value is no change, and nothing needs the variables now.
            Environment.SetEnvironmentVariable($"PGPROJ_VAR_{host}", "elsewhere.example");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // Options change in place.
            WriteSchema(projectDirectory, Edit(Edit(Project(role, password, host), "dbname 'app', port '5432'", "dbname 'shop'"), "user 'remote_user'", "user 'shop_user'"));
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, changed.ExitCode, changed.Output);
            Assert.AreEqual("host=db.example,dbname=shop", await ScalarTextAsync(connectionString, "SELECT array_to_string(srvoptions, ',') FROM pg_foreign_server WHERE srvname = 'remote'"));
            Assert.AreEqual("user=shop_user,password=first-secret", await OptionsAsync(connectionString, role), "The password was not sent again.");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A name no mapping uses (a typo) is refused before anything runs (G-347), and the mapping keeps its password.
            Environment.SetEnvironmentVariable($"PGPROJ_VAR_{password}", "second-secret");
            var misspelt = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--apply-secrets", password + "x");
            Assert.AreNotEqual(0, misspelt.ExitCode, misspelt.Output);
            StringAssert.Contains(misspelt.Output, $"--apply-secrets names :'{password}x', which no user mapping's options use, so nothing was run.");
            StringAssert.Contains(misspelt.Output, $":'{password}'");
            Assert.AreEqual("user=shop_user,password=first-secret", await OptionsAsync(connectionString, role));

            // --apply-secrets sends the current value to the mapping that exists.
            var applied = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--apply-secrets", password);
            Assert.AreEqual(0, applied.ExitCode, applied.Output);
            Assert.AreEqual("user=shop_user,password=second-secret", await OptionsAsync(connectionString, role));
        }
        finally
        {
            Environment.SetEnvironmentVariable($"PGPROJ_VAR_{password}", null);
            Environment.SetEnvironmentVariable($"PGPROJ_VAR_{host}", null);
            await DropDatabaseAsync(databaseName);
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP ROLE IF EXISTS {role};");
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task DroppingAMappingOrServerIsDestructiveAndAWrapperNeedsASuperuserFlag()
    {
        var databaseName = await CreateDatabaseAsync("g132d");
        var projectDirectory = CreateProjectDirectory("g132d");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            const string own = "CREATE FOREIGN DATA WRAPPER plain_fdw OPTIONS (debug 'true');\nCREATE SERVER local_one FOREIGN DATA WRAPPER plain_fdw;\nCREATE USER MAPPING FOR PUBLIC SERVER local_one;\n";
            WriteSchema(projectDirectory, own);

            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreNotEqual(0, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "a foreign data wrapper is created, changed, or dropped only by a superuser");

            var created = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-superuser-statements");
            Assert.AreEqual(0, created.ExitCode, created.Output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            WriteSchema(projectDirectory, "CREATE FOREIGN DATA WRAPPER plain_fdw OPTIONS (debug 'true');\n");
            var notAllowed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreNotEqual(0, notAllowed.ExitCode, notAllowed.Output);
            StringAssert.Contains(notAllowed.Output, "foreign server dropped");
            StringAssert.Contains(notAllowed.Output, "user mapping dropped");

            var dropped = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-destructive");
            Assert.AreEqual(0, dropped.ExitCode, dropped.Output);
            Assert.AreEqual("0", await ScalarTextAsync(connectionString, "SELECT count(*)::text FROM pg_foreign_server"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }

    [TestMethod]
    public async Task InitWritesThePasswordAsAVariableAndTheScaffoldConverges()
    {
        var role = NewName("g132i");
        var databaseName = await CreateDatabaseAsync("g132i");
        var secondDatabase = await CreateDatabaseAsync("g132j");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g132-init-" + Guid.NewGuid().ToString("N"));
        var variable = $"remote_{role}_password";
        try
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE ROLE {role} NOLOGIN;");
            var connectionString = ConnectionStringFor(databaseName);
            await ExecuteAsync(connectionString, $"""
                CREATE EXTENSION postgres_fdw;
                CREATE FOREIGN DATA WRAPPER plain_fdw;
                CREATE SERVER remote TYPE 'pg' VERSION '18' FOREIGN DATA WRAPPER postgres_fdw OPTIONS (host 'db.example', dbname 'app');
                COMMENT ON SERVER remote IS 'remote';
                GRANT USAGE ON FOREIGN SERVER remote TO {role};
                GRANT USAGE ON FOREIGN DATA WRAPPER postgres_fdw TO {role};
                CREATE USER MAPPING FOR {role} SERVER remote OPTIONS (user 'remote_user', password 'never-written');
                """);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString, "--report");
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            StringAssert.Contains(scaffolded.Output, $"PGPROJ_VAR_{variable}");

            // The report no longer lists what it scaffolds as not supported.
            var report = File.ReadAllText(Directory.EnumerateFiles(scaffoldDirectory, "init-report-*.md", SearchOption.AllDirectories).Single());
            var notSupported = report.IndexOf("## Not modeled: not supported", StringComparison.Ordinal) is var start and >= 0 ? report[start..] : string.Empty;
            notSupported = notSupported.IndexOf("\n## ", 1, StringComparison.Ordinal) is var end and > 0 ? notSupported[..end] : notSupported;
            foreach (var kind in new[] { "Foreign data wrapper", "Foreign server", "User mapping" })
            {
                Assert.IsFalse(notSupported.Contains(kind, StringComparison.Ordinal), notSupported);
            }
            var all = string.Concat(Directory.EnumerateFiles(scaffoldDirectory, "*.sql", SearchOption.AllDirectories).Select(File.ReadAllText));
            Assert.IsFalse(all.Contains("never-written", StringComparison.Ordinal), "A password is never written.\n" + all);
            var serverFile = File.ReadAllText(Path.Combine(scaffoldDirectory, "sql", "Server", "remote.sql"));
            StringAssert.Contains(serverFile, $"password :'{variable}'");
            StringAssert.Contains(serverFile, "COMMENT ON SERVER remote IS 'remote';");
            StringAssert.Contains(serverFile, $"GRANT USAGE ON FOREIGN SERVER remote TO {role}");
            Assert.IsTrue(File.Exists(Path.Combine(scaffoldDirectory, "sql", "ForeignDataWrapper", "plain_fdw.sql")));
            Assert.IsFalse(File.Exists(Path.Combine(scaffoldDirectory, "sql", "ForeignDataWrapper", "postgres_fdw.sql")), "An extension's wrapper is the extension's.");
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            Environment.SetEnvironmentVariable($"PGPROJ_VAR_{variable}", "the-secret");
            var secondConnection = ConnectionStringFor(secondDatabase);
            var copied = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute", "--allow-superuser-statements");
            Assert.AreEqual(0, copied.ExitCode, copied.Output);
            Assert.AreEqual("user=remote_user,password=the-secret", await ScalarTextAsync(secondConnection, $"SELECT array_to_string(umoptions, ',') FROM pg_user_mappings WHERE usename = '{role}'"));
            await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);
        }
        finally
        {
            Environment.SetEnvironmentVariable($"PGPROJ_VAR_{variable}", null);
            await DropDatabaseAsync(secondDatabase);
            await DropDatabaseAsync(databaseName);
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP ROLE IF EXISTS {role};");
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
