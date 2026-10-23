using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-284 against a real server (approved by the user 2026-10-13). A schema declared with only <c>CREATE SCHEMA</c> was not created by <c>publish</c>, and nothing said so;
/// Supabase declares its <c>graphql</c>, <c>storage</c>, and <c>vault</c> schemas that way for other tools to fill. It is now shown by <c>diff</c>, created, and converges,
/// with <c>schemas</c> in <c>pgproj.json</c> or without; a comment the database has on it is kept, and a schema the project stops declaring is not dropped.
/// </summary>
[TestClass]
public sealed class EmptySchemaEndToEndTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow(""", "schemas": ["b"]""")]
    public async Task AnEmptyDeclaredSchemaIsShownCreatedAndConverges(string extraProjectJson)
    {
        var databaseName = await CreateDatabaseAsync("g284");
        var projectDirectory = CreateProjectDirectory("g284", extraProjectJson);
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, "CREATE SCHEMA a;\nCREATE SCHEMA b;\nCREATE TABLE b.t (id int);\n");

            var diffed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            StringAssert.Contains(diffed.Output, "+ Schema a", diffed.Output);
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);
            Assert.AreEqual("a,b", await ScalarTextAsync(connectionString, "SELECT string_agg(nspname, ',' ORDER BY nspname) FROM pg_namespace WHERE nspname IN ('a', 'b')"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A comment set outside the project is the database's to keep, since the project states none.
            await ExecuteAsync(connectionString, "COMMENT ON SCHEMA a IS 'Filled by another tool.';");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A schema the project no longer declares is left in place.
            WriteSchema(projectDirectory, "CREATE SCHEMA b;\nCREATE TABLE b.t (id int);\n");
            Assert.AreEqual(0, (await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute")).ExitCode);
            Assert.AreEqual("Filled by another tool.", await ScalarTextAsync(connectionString, "SELECT obj_description('a'::regnamespace, 'pg_namespace')"));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
