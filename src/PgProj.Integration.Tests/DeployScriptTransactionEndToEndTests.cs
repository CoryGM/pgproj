using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-335 against a real server (the user's decisions of 2026-11-09): the finding was a pre-deploy script's <c>COMMIT</c>, which committed what ran before it
/// and left <c>--execute</c> failing with "Transaction is already completed", the earlier table left behind. Now <c>build</c> reports it at its line, and
/// <c>publish</c> refuses to run the project, so nothing is applied.
/// </summary>
[TestClass]
public sealed class DeployScriptTransactionEndToEndTests
{
    [TestMethod]
    public async Task ACommitInADeployScriptStopsThePublishBeforeAnythingRuns()
    {
        var databaseName = await CreateDatabaseAsync("g335");
        var directory = CreateProjectDirectory("g335", """, "predeploy": "./pre.sql", "postdeploy": "./post.sql" """);
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(directory);
            WriteSchema(directory, "CREATE TABLE public.t (id int PRIMARY KEY);\nCREATE TABLE public.after_op (id int);\n");
            File.WriteAllText(Path.Combine(directory, "pre.sql"), "CREATE TABLE public.before_commit (id int);\nCOMMIT;\n");
            File.WriteAllText(Path.Combine(directory, "post.sql"), "SELECT 1/0;\n");

            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreNotEqual(0, built.ExitCode, built.Output);
            StringAssert.Contains(built.Output, "pre.sql:2:1");
            StringAssert.Contains(built.Output, "COMMIT (or END) in a deploy script ends the transaction pgproj publishes in");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");

            Assert.AreNotEqual(0, published.ExitCode, published.Output);
            StringAssert.Contains(published.Output, "COMMIT (or END) in a deploy script");
            Assert.DoesNotContain("Transaction is already completed", published.Output);
            Assert.AreEqual(
                "0",
                await ScalarTextAsync(connectionString, "select count(*)::text from pg_class where relnamespace = 'public'::regnamespace and relkind = 'r'"),
                "nothing was applied, not even what came before the COMMIT");
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(directory);
        }
    }
}
