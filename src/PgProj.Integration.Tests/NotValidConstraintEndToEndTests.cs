using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-234 against a real server. A CHECK or FOREIGN KEY written <c>NOT VALID</c> was deployed as a valid constraint, so adding it to a table whose rows break
/// it failed. It is now added <c>NOT VALID</c>, read back, scaffolded by <c>init</c>, and validated in place when the project states it valid.
/// </summary>
[TestClass]
public sealed class NotValidConstraintEndToEndTests
{
    private const string Tables = """
        CREATE TABLE public.p (id int PRIMARY KEY);
        CREATE TABLE public.t (id int, b int, p_id int);
        """;

    [TestMethod]
    public async Task ANotValidCheckAndForeignKeyAreDeployedConvergeAreScaffoldedAndValidatedInPlace()
    {
        var databaseName = await CreateDatabaseAsync("g234");
        var projectDirectory = CreateProjectDirectory("g234");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g234-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            const string validated = "SELECT string_agg(conname || ':' || convalidated::text, ',' ORDER BY conname) FROM pg_constraint WHERE conrelid = 'public.t'::regclass";

            // Rows that break the constraints are already there.
            WriteSchema(projectDirectory, Tables);
            await PublishAsync(projectFile, connectionString);
            await ExecuteAsync(connectionString, "INSERT INTO public.t VALUES (1, 100, 7);");

            // NOT VALID: added although the row breaks both.
            const string notValid = Tables + """
                ALTER TABLE public.t ADD CONSTRAINT chk CHECK (b < 50) NOT VALID;
                ALTER TABLE public.t ADD CONSTRAINT fk FOREIGN KEY (p_id) REFERENCES public.p (id) NOT VALID;
                """;
            WriteSchema(projectDirectory, notValid);
            await PublishAsync(projectFile, connectionString);
            Assert.AreEqual("chk:false,fk:false", await ScalarTextAsync(connectionString, validated));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // init scaffolds them, and what it writes builds and converges.
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var built = await RunAsync("build", "--project", scaffoldProject);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            // The project states them valid, and the rows still break them: the database refuses, and nothing changes.
            WriteSchema(projectDirectory, Tables + """
                ALTER TABLE public.t ADD CONSTRAINT chk CHECK (b < 50);
                ALTER TABLE public.t ADD CONSTRAINT fk FOREIGN KEY (p_id) REFERENCES public.p (id);
                """);
            var changes = await RunAsync("diff", "--project", projectFile, "--target-connection", connectionString);
            StringAssert.Contains(changes.Output, "Not valid: ");
            var refused = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--execute");
            Assert.AreNotEqual(0, refused.ExitCode, "validating rows that break the constraints must fail.\n" + refused.Output);
            Assert.AreEqual("chk:false,fk:false", await ScalarTextAsync(connectionString, validated));

            // Once the rows are fixed, the same project validates them in place, without dropping them.
            await ExecuteAsync(connectionString, "UPDATE public.t SET b = 1; INSERT INTO public.p VALUES (7);");
            var oids = await ScalarTextAsync(connectionString, "SELECT string_agg(oid::text, ',' ORDER BY conname) FROM pg_constraint WHERE conrelid = 'public.t'::regclass");
            var script = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            StringAssert.Contains(script.Output, "VALIDATE CONSTRAINT");
            Assert.IsFalse(script.Output.Contains("DROP CONSTRAINT", StringComparison.Ordinal), script.Output);
            await PublishAsync(projectFile, connectionString);
            Assert.AreEqual("chk:true,fk:true", await ScalarTextAsync(connectionString, validated));
            Assert.AreEqual(oids, await ScalarTextAsync(connectionString, "SELECT string_agg(oid::text, ',' ORDER BY conname) FROM pg_constraint WHERE conrelid = 'public.t'::regclass"));
            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A project that still says NOT VALID is satisfied by a validated constraint: nothing to do.
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
