using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-147 against a real server. A role is cluster-wide, so pgproj never creates one: a grant that names a role the server lacks is refused by
/// <c>publish --execute</c> before anything runs, instead of failing with <c>42704</c> partway through, and a script that is only shown warns.
/// A role the pre-deploy script creates (the documented way: a guarded <c>DO</c> block) is not missing, and the project
/// converges.
/// </summary>
[TestClass]
public sealed class RoleEndToEndTests
{
    [TestMethod]
    public async Task AGrantNamingAMissingRoleIsRefusedBeforeAnythingRunsAndADryRunOnlyWarns()
    {
        var databaseName = await CreateDatabaseAsync("g147m");
        var projectDirectory = CreateProjectDirectory("g147m");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, "CREATE SCHEMA app;\nCREATE TABLE app.t (a integer);\nGRANT SELECT ON TABLE app.t TO g147_missing_role;\nGRANT USAGE ON SCHEMA app TO PUBLIC;\nGRANT g147_missing_group TO g147_missing_role;\n");

            var dryRun = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.AreEqual(0, dryRun.ExitCode, dryRun.Output);
            StringAssert.Contains(dryRun.Output, "Warning: the project's grants and owners name roles the server does not have, so this script cannot run on it:");
            StringAssert.Contains(dryRun.Output, "role \"g147_missing_role\", named by role:g147_missing_group");

            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(1, refused.ExitCode, refused.Output);
            StringAssert.Contains(refused.Output, "Publish failed: the project's grants and owners name roles the server does not have, so the script was not run:");
            StringAssert.Contains(refused.Output, "role \"g147_missing_role\"");
            StringAssert.Contains(refused.Output, "role \"g147_missing_group\"");
            StringAssert.Contains(refused.Output, "table:app.t");
            Assert.IsFalse(refused.Output.Contains("\"PUBLIC\"", StringComparison.Ordinal), "PUBLIC is not a role.\n" + refused.Output);
            StringAssert.Contains(refused.Output, "pgproj does not create roles");
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

    [TestMethod]
    public async Task ARoleThePreDeployScriptCreatesIsAcceptedDeployedAndConverges()
    {
        const string role = "g147_made_by_script";
        var databaseName = await CreateDatabaseAsync("g147p");
        var projectDirectory = CreateProjectDirectory("g147p", ", \"predeploy\": \"./pre.sql\"");
        try
        {
            // The documented shape of a pre-deploy role script: guarded, so it is safe to run again.
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "pre.sql"), $$"""
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = '{{role}}') THEN
                        CREATE ROLE {{role}} NOLOGIN;
                    END IF;
                END
                $$;
                """);
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, $"CREATE SCHEMA app;\nCREATE TABLE app.t (a integer);\nGRANT SELECT ON TABLE app.t TO {role};\n");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, published.Output);
            Assert.IsFalse(published.Output.Contains("roles the server does not have", StringComparison.Ordinal), published.Output);
            Assert.AreEqual("t", await ScalarTextAsync(connectionString, $"SELECT table_name FROM information_schema.role_table_grants WHERE grantee = '{role}' AND table_schema = 'app';"));

            // The role now exists on the server, so the second publish takes the other path, and nothing is left to change.
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, again.ExitCode, again.Output);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP ROLE IF EXISTS {role};");
            TryDeleteDirectory(projectDirectory);
        }
    }
}
