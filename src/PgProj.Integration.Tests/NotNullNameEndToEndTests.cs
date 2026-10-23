using static PgProj.Integration.Tests.EndToEndSupport;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-235 against a real server, on every version. On PostgreSQL 18 a named NOT NULL keeps its name and its comment: they are deployed, read back, compared
/// (a different name is renamed in place, keeping the constraint), inherited by a child as its parent's, and scaffolded by <c>init</c>. An older server
/// stores no name, so there the same project must still publish and converge.
/// </summary>
[TestClass]
public sealed class NotNullNameEndToEndTests
{
    private const string Schema = """
        CREATE TABLE public.t (a int CONSTRAINT nn NOT NULL, b int NOT NULL, c int);
        COMMENT ON CONSTRAINT nn ON public.t IS 'the a';
        CREATE TABLE public.p (a int CONSTRAINT pnn NOT NULL);
        CREATE TABLE public.c1 () INHERITS (public.p);
        CREATE TABLE public.c2 (a int CONSTRAINT own NOT NULL) INHERITS (public.p);
        """;

    [TestMethod]
    public async Task ANamedNotNullIsDeployedConvergesIsRenamedInPlaceInheritedAndScaffolded()
    {
        var eighteen = await ServerMajorAsync() >= 18;
        var databaseName = await CreateDatabaseAsync("g235");
        var projectDirectory = CreateProjectDirectory("g235");
        var scaffoldDirectory = Path.Combine(Path.GetTempPath(), "pgproj-g235-init-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = ConnectionStringFor(databaseName);
            var projectFile = ProjectFile(projectDirectory);
            const string names = "SELECT string_agg(conrelid::regclass::text || '.' || conname, ',' ORDER BY conrelid::regclass::text, conname) FROM pg_constraint WHERE contype = 'n' AND connamespace = 'public'::regnamespace";

            WriteSchema(projectDirectory, Schema);
            await PublishAsync(projectFile, connectionString);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
            if (eighteen)
            {
                Assert.AreEqual("c1.pnn,c2.own,p.pnn,t.nn,t.t_b_not_null", await ScalarTextAsync(connectionString, names));
                Assert.AreEqual("the a", await ScalarTextAsync(connectionString, "SELECT obj_description(oid, 'pg_constraint') FROM pg_constraint WHERE conname = 'nn'"));
            }

            // A second publish changes nothing.
            var again = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
            Assert.AreEqual(0, again.ExitCode, again.Output);
            Assert.IsFalse(again.Output.Contains("CONSTRAINT", StringComparison.Ordinal), again.Output);

            // Another name is a rename of the same constraint, and the comment stays.
            var renamed = Schema.Replace("CONSTRAINT nn ", "CONSTRAINT nn2 ", StringComparison.Ordinal).Replace("COMMENT ON CONSTRAINT nn ON", "COMMENT ON CONSTRAINT nn2 ON", StringComparison.Ordinal);
            WriteSchema(projectDirectory, renamed);
            if (eighteen)
            {
                var oid = await ScalarTextAsync(connectionString, "SELECT oid::text FROM pg_constraint WHERE conname = 'nn'");
                var script = await RunAsync("publish", "--project", projectFile, "--target-connection", connectionString, "--dry-run");
                StringAssert.Contains(script.Output, "RENAME CONSTRAINT \"nn\" TO \"nn2\"");
                await PublishAsync(projectFile, connectionString);
                Assert.AreEqual(oid, await ScalarTextAsync(connectionString, "SELECT oid::text FROM pg_constraint WHERE conname = 'nn2'"));
                Assert.AreEqual("the a", await ScalarTextAsync(connectionString, "SELECT obj_description(oid, 'pg_constraint') FROM pg_constraint WHERE conname = 'nn2'"));
            }
            else
            {
                await PublishAsync(projectFile, connectionString);
            }

            await AssertDiffIsEmptyAsync(projectFile, connectionString);

            // A column that becomes NOT NULL is given the name and the comment.
            var becomes = renamed.Replace("c int);", "c int CONSTRAINT nn_c NOT NULL);", StringComparison.Ordinal) + "\nCOMMENT ON CONSTRAINT nn_c ON public.t IS 'the c';";
            WriteSchema(projectDirectory, becomes);
            await PublishAsync(projectFile, connectionString);
            await AssertDiffIsEmptyAsync(projectFile, connectionString);
            if (eighteen)
            {
                Assert.AreEqual("the c", await ScalarTextAsync(connectionString, "SELECT obj_description(oid, 'pg_constraint') FROM pg_constraint WHERE conname = 'nn_c'"));
            }

            // init scaffolds the names and the comments, and what it writes builds and converges; published to another database it makes the same constraints.
            var scaffoldProject = Path.Combine(scaffoldDirectory, "pgproj.json");
            var scaffolded = await RunAsync("init", "--project", scaffoldProject, "--source-connection", connectionString);
            Assert.AreEqual(0, scaffolded.ExitCode, scaffolded.Output);
            var built = await RunAsync("build", "--project", scaffoldProject);
            Assert.AreEqual(0, built.ExitCode, built.Output);
            await AssertDiffIsEmptyAsync(scaffoldProject, connectionString);

            var secondDatabase = await CreateDatabaseAsync("g235b");
            try
            {
                var secondConnection = ConnectionStringFor(secondDatabase);
                var republished = await RunAsync("publish", "--project", scaffoldProject, "--target-connection", secondConnection, "--execute");
                Assert.AreEqual(0, republished.ExitCode, republished.Output);
                Assert.AreEqual(await ScalarTextAsync(connectionString, names), await ScalarTextAsync(secondConnection, names));
                await AssertDiffIsEmptyAsync(scaffoldProject, secondConnection);
            }
            finally
            {
                await DropDatabaseAsync(secondDatabase);
            }
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
