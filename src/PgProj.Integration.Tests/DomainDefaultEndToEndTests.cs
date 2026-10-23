using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-244 against a real server. <c>init</c> wrote a domain's default as <c>pg_type.typdefault</c>, the text as it was when the domain was created, so a function of another
/// schema lost its schema (<c>DEFAULT foo(4)</c>) and the scaffold could not be published. It is now rendered by <c>pg_get_expr</c>, as every other expression is.
/// </summary>
[TestClass]
public sealed class DomainDefaultEndToEndTests
{
    [TestMethod]
    public async Task ADomainDefaultThatCallsAFunctionOfAnotherSchemaIsScaffoldedWithItsSchema()
    {
        var databaseName = await CreateDatabaseAsync("g244");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g244-init-" + Guid.NewGuid().ToString("N"));
        var secondDatabase = await CreateDatabaseAsync("g244b");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            // The domain is created with the function's schema on the search path, which is when PostgreSQL stores the default's text.
            await ExecuteAsync(connectionString, """
                CREATE SCHEMA s;
                CREATE FUNCTION s.foo(int) RETURNS int LANGUAGE sql IMMUTABLE AS 'SELECT $1 + 1';
                SET search_path = s;
                CREATE DOMAIN s.d AS int DEFAULT foo(4);
                CREATE DOMAIN s.t AS text DEFAULT 'abc';
                CREATE DOMAIN s.n AS numeric(10, 2) DEFAULT 1.5;
                CREATE TABLE s.tbl (a int DEFAULT foo(5), b s.d, c s.t, e s.n);
                """);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var built = await RunAsync("build", "--project", scaffoldProject);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            var secondConnection = ConnectionStringFor(secondDatabase);
            var published = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publishing the scaffold failed.\n" + published.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);
            Assert.AreEqual("5|abc|1.50", await ScalarTextAsync(secondConnection, "SET search_path = s; INSERT INTO s.tbl DEFAULT VALUES; SELECT (SELECT b FROM s.tbl)::text || '|' || (SELECT c FROM s.tbl) || '|' || (SELECT e FROM s.tbl)::text"));
        }
        finally
        {
            await DropDatabaseAsync(secondDatabase);
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
