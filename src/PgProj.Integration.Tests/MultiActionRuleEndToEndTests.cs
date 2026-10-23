using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-230 against a real server. A rule with several actions, <c>DO ALSO (INSERT ...; INSERT ...)</c>, was cut at the semicolon inside its parentheses when
/// <c>publish --execute</c> split the script, and failed with a syntax error. It is now deployed whole, behaves, and converges.
/// </summary>
[TestClass]
public sealed class MultiActionRuleEndToEndTests
{
    [TestMethod]
    public async Task ARuleWithSeveralActionsIsDeployedWholeAndConverges()
    {
        var databaseName = await CreateDatabaseAsync("g230");
        var projectDirectory = CreateProjectDirectory("g230");
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, """
                CREATE TABLE public.t (a int);
                CREATE TABLE public.log (a int);
                CREATE RULE r AS ON INSERT TO public.t DO ALSO (INSERT INTO public.log VALUES (new.a); INSERT INTO public.log VALUES (new.a + 1));
                """);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            await ExecuteAsync(connectionString, "INSERT INTO public.t VALUES (10);");
            Assert.AreEqual("10,11", await ScalarTextAsync(connectionString, "SELECT string_agg(a::text, ',' ORDER BY a) FROM public.log;"));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
        }
    }
}
