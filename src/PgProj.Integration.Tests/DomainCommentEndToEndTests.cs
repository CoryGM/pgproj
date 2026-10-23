using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-302 against a real server: a domain's comment is read back from the database, so a published comment converges, a changed one is found and
/// applied, <c>COMMENT ON DOMAIN</c> is the same comment as <c>COMMENT ON TYPE</c>, and <c>init</c> scaffolds it.
/// </summary>
[TestClass]
public sealed class DomainCommentEndToEndTests
{
    private const string Domain = """
        CREATE SCHEMA app;
        CREATE DOMAIN app.amount AS numeric(12,2) CHECK (VALUE >= 0);
        CREATE TABLE app.t (id int PRIMARY KEY, a app.amount);
        """;

    [TestMethod]
    public async Task ADomainCommentConvergesChangesAndIsScaffolded()
    {
        var databaseName = await CreateDatabaseAsync("g302");
        var projectDirectory = CreateProjectDirectory("g302");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), $"pgproj-g302s-{Guid.NewGuid():N}");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            Task<string> CommentAsync() => ScalarTextAsync(connectionString, "select coalesce(obj_description('app.amount'::regtype, 'pg_type'), '(none)')");

            WriteSchema(projectDirectory, Domain + "COMMENT ON TYPE app.amount IS 'An amount.';");
            var first = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, first.ExitCode, first.Output);
            Assert.AreEqual("An amount.", await CommentAsync());
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            WriteSchema(projectDirectory, Domain + "COMMENT ON DOMAIN app.amount IS 'An amount.';");
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            WriteSchema(projectDirectory, Domain + "COMMENT ON DOMAIN app.amount IS 'A non-negative amount.';");
            var changed = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            StringAssert.Contains(changed.Output, "Comment: 'An amount.' -> 'A non-negative amount.'");
            var second = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, second.ExitCode, second.Output);
            Assert.AreEqual("A non-negative amount.", await CommentAsync());
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffold = await RunAsync("init", "--source-connection", connectionString, "--project", Path.Combine(scaffoldDirectory, "pgproj.json"));
            Assert.AreEqual(0, scaffold.ExitCode, scaffold.Output);
            var domainFile = Directory.EnumerateFiles(scaffoldDirectory, "amount.sql", SearchOption.AllDirectories).Single();
            StringAssert.Contains(File.ReadAllText(domainFile), "COMMENT ON TYPE app.amount IS 'A non-negative amount.';");
            await AssertDiffIsEmptyAsync(Path.Combine(scaffoldDirectory, "pgproj.json"), connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
