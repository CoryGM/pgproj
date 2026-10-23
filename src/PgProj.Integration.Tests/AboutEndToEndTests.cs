using Npgsql;
using PgProj.Engine;
using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-332 against a real server, on every version: <c>pgproj about</c> connects to the target and the source, reads the server's version and the role's
/// privileges (a superuser, and a role that may not create temporary objects), and reports a refused login with its reason and without the password.
/// </summary>
[TestClass]
public sealed class AboutEndToEndTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task AConnectedDatabaseGivesItsVersionAndTheRolesPrivileges()
    {
        var databaseName = await CreateDatabaseAsync("g332_about");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);

            var result = await new PgProjEngine().AboutAsync(
                new AboutRequest { TargetConnectionString = connectionString, TargetConnectionOrigin = "--target-connection", SourceConnectionString = connectionString, SourceConnectionOrigin = "--source-connection" },
                TestContext.CancellationToken);

            foreach (var connection in new[] { result.Target!, result.Source! })
            {
                Assert.AreEqual(AboutConnectionStatus.Connected, connection.Status, connection.Message);
                StringAssert.StartsWith(connection.ServerVersion, (await ServerMajorAsync()).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
                Assert.AreEqual(databaseName, connection.Database);
                Assert.AreEqual(new NpgsqlConnectionStringBuilder(connectionString).Username, connection.Username);
                Assert.IsTrue(connection.IsSuperuser);
                Assert.IsTrue(connection.CanCreateTemporaryObjects);
            }
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
        }
    }

    [TestMethod]
    public async Task ARoleThatMayNotCreateTemporaryObjectsIsSaidToAndARefusedLoginGivesItsReason()
    {
        var databaseName = await CreateDatabaseAsync("g332_role");
        var role = "g332_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"CREATE ROLE {role} LOGIN PASSWORD 'g332secret'; REVOKE TEMPORARY ON DATABASE {databaseName} FROM PUBLIC; GRANT CONNECT ON DATABASE {databaseName} TO {role};");
            var asRole = new NpgsqlConnectionStringBuilder(ConnectionStringFor(databaseName)) { Username = role, Password = "g332secret" }.ConnectionString;
            var wrongPassword = new NpgsqlConnectionStringBuilder(asRole) { Password = "notthepassword" }.ConnectionString;

            var result = await new PgProjEngine().AboutAsync(
                new AboutRequest { TargetConnectionString = asRole, TargetConnectionOrigin = "--target-connection", SourceConnectionString = wrongPassword, SourceConnectionOrigin = "--source-connection" },
                TestContext.CancellationToken);

            Assert.AreEqual(AboutConnectionStatus.Connected, result.Target!.Status, result.Target.Message);
            Assert.IsFalse(result.Target.IsSuperuser);
            Assert.IsFalse(result.Target.CanCreateTemporaryObjects);
            Assert.AreEqual(role, result.Target.Username);
            Assert.AreEqual(AboutConnectionStatus.Failed, result.Source!.Status);
            StringAssert.Contains(result.Source.Message, "28P01");
            Assert.AreEqual(role, result.Source.Username);
            foreach (var secret in new[] { "g332secret", "notthepassword" })
            {
                Assert.IsFalse(result.Target.ToString().Contains(secret, StringComparison.Ordinal), secret);
                Assert.IsFalse(result.Source.ToString().Contains(secret, StringComparison.Ordinal), secret);
            }
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            await ExecuteAsync(PostgresContainerFixture.ConnectionString, $"DROP ROLE IF EXISTS {role};");
        }
    }

    [TestMethod]
    public async Task TheCommandPrintsTheServerAndTheProjectAndExitsZero()
    {
        var databaseName = await CreateDatabaseAsync("g332_cli");
        var directory = CreateProjectDirectory("g332_cli");
        try
        {
            WriteSchema(directory, "CREATE TABLE public.t (id int);");
            var connectionString = ConnectionStringFor(databaseName);

            var run = await RunAsync("about", "--project", ProjectFile(directory), "--target-connection", connectionString);

            Assert.AreEqual(0, run.ExitCode, run.Output);
            StringAssert.Contains(run.Output, "Target database: from --target-connection");
            StringAssert.Contains(run.Output, $"database {databaseName}");
            StringAssert.Contains(run.Output, $"  Server: PostgreSQL {await ServerMajorAsync()}.");
            StringAssert.Contains(run.Output, "  Superuser: yes; can create temporary objects: yes");
            StringAssert.Contains(run.Output, "(1 SQL file)");
            // The container's password is its user's name, which is printed; the connection string's password setting is not.
            Assert.IsFalse(run.Output.Contains("Password=", StringComparison.OrdinalIgnoreCase), run.Output);
        }
        finally
        {
            TryDeleteDirectory(directory);
            await DropDatabaseAsync(databaseName);
        }
    }
}
