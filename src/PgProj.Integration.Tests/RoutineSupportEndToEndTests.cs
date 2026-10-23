using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-205 against a real server. A routine's <c>SUPPORT</c> function was left out of <c>CREATE FUNCTION</c> and not compared. It is now deployed as
/// written, read back from <c>pg_get_functiondef</c>, compared (its <c>pg_catalog</c> schema aside), scaffolded by <c>init</c>, and removed in place.
/// <c>TRANSFORM</c> is covered by the unit tests: it applies only to a language pgproj does not accept yet (plperl, plpython).
/// </summary>
[TestClass]
public sealed class RoutineSupportEndToEndTests
{
    private const string Sql = """
        CREATE FUNCTION public.f(text, text) RETURNS bool LANGUAGE internal IMMUTABLE STRICT SUPPORT textlike_support AS 'textlike';
        CREATE FUNCTION public.h(text, text) RETURNS bool LANGUAGE internal IMMUTABLE STRICT SUPPORT pg_catalog.textlike_support AS 'textlike';
        """;

    private const string Supports = "SELECT string_agg(proname || ':' || prosupport::text, ' ' ORDER BY proname) FROM pg_proc WHERE pronamespace = 'public'::regnamespace;";

    [TestMethod]
    public async Task ASupportFunctionIsDeployedAndConverges_AndIsRemovedInPlace()
    {
        var databaseName = await CreateDatabaseAsync("g205");
        var projectDirectory = CreateProjectDirectory("g205");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g205-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, Sql);

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-superuser-statements");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("f:textlike_support h:textlike_support", await ScalarTextAsync(connectionString, Supports));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            WriteSchema(projectDirectory, Edit(Sql, "STRICT SUPPORT textlike_support AS", "STRICT AS"));
            var changed = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute", "--allow-superuser-statements");
            Assert.AreEqual(0, changed.ExitCode, "publish --execute failed.\n" + changed.Output);
            Assert.AreEqual("f:- h:textlike_support", await ScalarTextAsync(connectionString, Supports));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
