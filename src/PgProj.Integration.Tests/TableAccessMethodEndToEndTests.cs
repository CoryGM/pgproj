using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-223 against a real server. A table's <c>USING</c> access method was dropped, so a table meant for another access method was created as a heap table. It is
/// now deployed as written, converges, is changed in place, and is scaffolded when it is not the server's default.
/// </summary>
[TestClass]
public sealed class TableAccessMethodEndToEndTests
{
    [TestMethod]
    [PostgreSqlVersion(Minimum = 15)]
    public async Task TheAccessMethodIsDeployedChangedInPlaceAndScaffolded()
    {
        var databaseName = await CreateDatabaseAsync("g223");
        var projectDirectory = CreateProjectDirectory("g223");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g223-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            // A second table access method, which pgproj does not model: it is the database's, made here as a DBA would.
            await ExecuteAsync(connectionString, "CREATE ACCESS METHOD heap2 TYPE TABLE HANDLER heap_tableam_handler;");
            var projectFile = ProjectFile(projectDirectory);
            WriteSchema(projectDirectory, "CREATE TABLE public.t (a int) USING heap2;\nCREATE TABLE public.u (a int) USING heap;\nCREATE TABLE public.plain (a int);\n");

            var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
            Assert.AreEqual("heap2", await ScalarTextAsync(connectionString, "SELECT am.amname FROM pg_class c JOIN pg_am am ON am.oid = c.relam WHERE c.oid = 'public.t'::regclass;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            await ExecuteAsync(connectionString, "INSERT INTO public.u VALUES (1), (2);");
            WriteSchema(projectDirectory, "CREATE TABLE public.t (a int) USING heap2;\nCREATE TABLE public.u (a int) USING heap2;\nCREATE TABLE public.plain (a int);\n");
            var altered = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreEqual(0, altered.ExitCode, "the second publish failed.\n" + altered.Output);
            Assert.AreEqual("heap2 2", await ScalarTextAsync(connectionString, "SELECT am.amname || ' ' || (SELECT count(*) FROM public.u) FROM pg_class c JOIN pg_am am ON am.oid = c.relam WHERE c.oid = 'public.u'::regclass;"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            StringAssert.Contains(File.ReadAllText(Directory.GetFiles(scaffoldDirectory, "u.sql", SearchOption.AllDirectories).Single()), "USING heap2");
            Assert.IsFalse(File.ReadAllText(Directory.GetFiles(scaffoldDirectory, "plain.sql", SearchOption.AllDirectories).Single()).Contains("USING", StringComparison.Ordinal));
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }
}
