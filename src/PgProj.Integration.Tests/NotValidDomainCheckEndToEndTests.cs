using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-233 against a real server. A domain's <c>NOT VALID</c> check made <c>init</c> fail for the whole database, because the live read wrote it inside
/// <c>CREATE DOMAIN</c>, which has no such clause. It is now written as <c>ALTER DOMAIN ... ADD CONSTRAINT ... NOT VALID</c>, which a project can state too:
/// deployed, read back, scaffolded, and validated in place when the project states it valid.
/// </summary>
[TestClass]
public sealed class NotValidDomainCheckEndToEndTests
{
    private const string Base = """
        CREATE DOMAIN public.d AS int;
        CREATE TABLE public.t (x public.d);
        """;

    [TestMethod]
    public async Task ANotValidDomainCheckIsDeployedConvergesIsScaffoldedAndValidatedInPlace()
    {
        var databaseName = await CreateDatabaseAsync("g233");
        var projectDirectory = CreateProjectDirectory("g233");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g233-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            const string state = "SELECT string_agg(conname || ':' || convalidated::text, ',' ORDER BY conname) FROM pg_constraint WHERE contypid = 'public.d'::regtype AND contype = 'c'";

            // A row that will break the check is already there.
            WriteSchema(projectDirectory, Base);
            await PublishAsync(projectFile, connectionString);
            await ExecuteAsync(connectionString, "INSERT INTO public.t VALUES (-5);");

            // NOT VALID: added although the row breaks it.
            const string notValid = Base + "\nALTER DOMAIN public.d ADD CONSTRAINT pos CHECK (VALUE > 0) NOT VALID;";
            WriteSchema(projectDirectory, notValid);
            var built = await RunAsync("build", "--project", projectFile);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            Assert.IsFalse(built.Output.Contains("is not modeled", StringComparison.Ordinal), built.Output);
            await PublishAsync(projectFile, connectionString);
            Assert.AreEqual("pos:false", await ScalarTextAsync(connectionString, state));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // init no longer fails for the database, and what it writes builds and converges.
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var scaffoldBuilt = await RunAsync("build", "--project", scaffoldProject);
            Assert.AreEqual(0, scaffoldBuilt.ExitCode, scaffoldBuilt.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            // The project states it valid; the row still breaks it, so the database refuses and nothing changes.
            const string valid = Base + "\nALTER DOMAIN public.d ADD CONSTRAINT pos CHECK (VALUE > 0);";
            WriteSchema(projectDirectory, valid);
            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreNotEqual(0, refused.ExitCode, refused.Output);
            Assert.AreEqual("pos:false", await ScalarTextAsync(connectionString, state));

            // Once the row is fixed, the same project validates it in place.
            await ExecuteAsync(connectionString, "UPDATE public.t SET x = 5;");
            var oid = await ScalarTextAsync(connectionString, "SELECT oid::text FROM pg_constraint WHERE conname = 'pos' AND contypid = 'public.d'::regtype");
            var script = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            StringAssert.Contains(script.Output, "VALIDATE CONSTRAINT");
            Assert.IsFalse(script.Output.Contains("DROP CONSTRAINT", StringComparison.Ordinal), script.Output);
            await PublishAsync(projectFile, connectionString);
            Assert.AreEqual("pos:true", await ScalarTextAsync(connectionString, state));
            Assert.AreEqual(oid, await ScalarTextAsync(connectionString, "SELECT oid::text FROM pg_constraint WHERE conname = 'pos' AND contypid = 'public.d'::regtype"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A project that still says NOT VALID is satisfied by the validated check.
            WriteSchema(projectDirectory, notValid);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
            TryDeleteDirectory(projectDirectory);
            TryDeleteDirectory(scaffoldDirectory);
        }
    }

    private static async Task PublishAsync(string projectFile, string connectionString)
    {
        var published = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
        Assert.AreEqual(0, published.ExitCode, "publish --execute failed.\n" + published.Output);
    }
}
